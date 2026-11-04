using System.Runtime.ExceptionServices;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using static SQLitePCL.raw;

namespace NextGameAI.Web.Services;

public sealed partial class GameEnrichmentService
{
    private enum WriteStage { BeforeCommit, Committing, Confirmed }
    private sealed class WriteAttempt
    {
        public WriteStage Stage;
        public bool CleanupCompleted = true;
        public Exception? Error;
        public GameEnrichmentResult? Result;
        public readonly List<CatalogMetadataSection> Sections = [];
        public readonly List<TaxonomyEnrichmentResult> Results = [];
        public readonly List<GameEnrichmentSectionDecision> Decisions = [];
        public readonly Dictionary<CatalogMetadataSection, EnrichmentCooldownObservation> Observations = [];
    }

    private async Task<GameEnrichmentResult> RecoverWriteAsync(int gameId, long igdbId,
        GameEnrichmentCoordinator.GameLease gameLease, Dictionary<CatalogMetadataSection, GameEnrichmentState> initial,
        Work[] plans, Dictionary<CatalogMetadataSection, EnrichmentCooldownObservation> observations,
        Dictionary<CatalogMetadataSection, EnrichmentAttemptId> ids, DateTime clientCompleted,
        List<GameEnrichmentSectionDecision> decisions, CancellationToken ct)
    {
        for (var number = 1; number <= 2; number++)
        {
            ct.ThrowIfCancellationRequested();
            var attempt = await WriteOnceAsync(gameId, igdbId, gameLease, initial, plans, observations, ids, clientCompleted, decisions, ct);
            if (attempt.Stage == WriteStage.Confirmed)
                return PublishCommitted(attempt) with { WriteAttempts = number };
            if (attempt.Stage == WriteStage.Committing)
                return Batch(GameEnrichmentOutcome.CommitIndeterminate, [], attempt.Decisions.Concat(attempt.Sections.Select(s =>
                    new GameEnrichmentSectionDecision(s, GameEnrichmentDisposition.CommitIndeterminate)))) with
                { WriteAttempts = number, CommitState = EnrichmentCommitState.Indeterminate, CleanupCompleted = attempt.CleanupCompleted };
            if (attempt.Error is OperationCanceledException cancelled)
                ExceptionDispatchInfo.Capture(cancelled).Throw();
            if (attempt.Error is null && attempt.CleanupCompleted)
                return attempt.Result! with { WriteAttempts = number };

            var failure = ClassifySqlite(attempt.Error);
            ct.ThrowIfCancellationRequested();
            if (number == 1 && attempt.CleanupCompleted && failure is EnrichmentWriteFailure.Busy or EnrichmentWriteFailure.Locked)
            {
                // Previous transaction, context and global writer are gone. Keep only the game lease.
                try { await Task.Delay(TimeSpan.FromMilliseconds(200), clock, ct); }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) when (ManagedFailure(error))
                {
                    ct.ThrowIfCancellationRequested();
                    return PersistenceFailed(attempt, ids, number, EnrichmentWriteFailure.Unknown);
                }
                continue;
            }
            return PersistenceFailed(attempt, ids, number, failure);
        }
        throw new InvalidOperationException("Limite de tentativas inválido.");
    }

    private GameEnrichmentResult PersistenceFailed(WriteAttempt attempt,
        Dictionary<CatalogMetadataSection, EnrichmentAttemptId> ids, int number, EnrichmentWriteFailure failure)
    {
        foreach (var section in attempt.Sections) cooldown.RecordPersistenceFailure(ids[section], clock.GetUtcNow().UtcDateTime);
        return Batch(GameEnrichmentOutcome.PersistenceFailure, [], attempt.Decisions.Concat(attempt.Sections.Select(s =>
            new GameEnrichmentSectionDecision(s, GameEnrichmentDisposition.PersistenceFailure)))) with
        { WriteAttempts = number, WriteFailure = failure, CleanupCompleted = attempt.CleanupCompleted };
    }

    private async Task<WriteAttempt> WriteOnceAsync(int gameId, long igdbId, GameEnrichmentCoordinator.GameLease gameLease,
        Dictionary<CatalogMetadataSection, GameEnrichmentState> initial, Work[] plans,
        Dictionary<CatalogMetadataSection, EnrichmentCooldownObservation> observations,
        Dictionary<CatalogMetadataSection, EnrichmentAttemptId> ids, DateTime clientCompleted,
        List<GameEnrichmentSectionDecision> decisions, CancellationToken ct)
    {
        var attempt = new WriteAttempt();
        attempt.Sections.AddRange(plans.Select(p => p.Result.Section));
        attempt.Decisions.AddRange(decisions);
        foreach (var pair in observations) attempt.Observations.Add(pair.Key, pair.Value);
        GameEnrichmentCoordinator.GameLease.WriteLease? writer = null;
        IServiceScope? scope = null;
        IDbContextTransaction? transaction = null;
        try
        {
            writer = await gameLease.AcquireWriteAsync(ct);
            ct.ThrowIfCancellationRequested();
            scope = scopes.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            transaction = await db.Database.BeginTransactionAsync(ct);
            if (!await Matches(db, gameId, igdbId, ct))
            {
                attempt.Result = Batch(GameEnrichmentOutcome.IdentityMismatch, [], attempt.Decisions.Concat(attempt.Sections.Select(s =>
                    new GameEnrichmentSectionDecision(s, GameEnrichmentDisposition.DiscardedAfterRevalidation))));
                return attempt;
            }
            var current = await db.GameEnrichmentStates.Where(s => s.GameId == gameId).ToDictionaryAsync(s => s.Section, ct);
            var allowed = new List<Work>();
            foreach (var plan in plans)
            {
                var section = plan.Result.Section;
                var blocked = Blocked(gameId, section, current.GetValueOrDefault(section));
                if (!SameState(initial.GetValueOrDefault(section), current.GetValueOrDefault(section)) || blocked is not null || clock.GetUtcNow().UtcDateTime < clientCompleted)
                    attempt.Decisions.Add(new(section, GameEnrichmentDisposition.DiscardedAfterRevalidation, blocked?.NextAllowedAtUtc));
                else allowed.Add(plan);
            }
            attempt.Sections.Clear(); attempt.Sections.AddRange(allowed.Select(p => p.Result.Section));
            if (allowed.Count == 0)
            { attempt.Result = Batch(GameEnrichmentOutcome.Preserved, [], attempt.Decisions); return attempt; }
            foreach (var plan in allowed)
            {
                var result = plan.Result;
                if (result.Outcome == TaxonomyEnrichmentOutcome.Applied && !await plan.Persist!(db, gameId, ct))
                {
                    result = result with { Outcome = TaxonomyEnrichmentOutcome.InvalidResponse };
                    attempt.Observations[result.Section] = cooldown.PrepareResult(ids[result.Section], clientCompleted, clientCompleted, result)!;
                }
                var state = current.GetValueOrDefault(result.Section);
                if (state is null)
                { state = new GameEnrichmentState { GameId = gameId, Section = result.Section }; db.GameEnrichmentStates.Add(state); }
                state.LastAttemptAtUtc = clientCompleted; state.Source = "IGDB";
                state.LastResult = result.Outcome switch
                {
                    TaxonomyEnrichmentOutcome.Applied => CatalogUpdateResult.Success,
                    TaxonomyEnrichmentOutcome.GameNotFound => CatalogUpdateResult.NotFound,
                    _ => CatalogUpdateResult.TemporaryFailure
                };
                if (state.LastResult == CatalogUpdateResult.Success) state.LastSuccessAtUtc = clientCompleted;
                attempt.Results.Add(result);
            }
            await db.SaveChangesAsync(ct);
            if (!await Matches(db, gameId, igdbId, ct))
            {
                attempt.Result = Batch(GameEnrichmentOutcome.IdentityMismatch, [], attempt.Decisions.Concat(attempt.Sections.Select(s =>
                    new GameEnrichmentSectionDecision(s, GameEnrichmentDisposition.DiscardedAfterRevalidation))));
                return attempt;
            }
            ct.ThrowIfCancellationRequested();
            attempt.Stage = WriteStage.Committing;
            await transaction.CommitAsync(ct);
            attempt.Stage = WriteStage.Confirmed;
        }
        catch (Exception error) when (ManagedFailure(error)) { attempt.Error = error; }
        finally
        {
            // A failed rollback/cleanup prevents retry. Cancellation cannot interrupt resource cleanup.
            if (transaction is not null && attempt.Stage == WriteStage.BeforeCommit)
            {
                try { await transaction.RollbackAsync(CancellationToken.None); }
                catch (Exception error) when (ManagedFailure(error)) { attempt.CleanupCompleted = false; attempt.Error ??= error; }
            }
            if (transaction is not null)
            {
                try { await transaction.DisposeAsync(); }
                catch (Exception error) when (ManagedFailure(error)) { attempt.CleanupCompleted = false; attempt.Error ??= error; }
            }
            try { scope?.Dispose(); }
            catch (Exception error) when (ManagedFailure(error)) { attempt.CleanupCompleted = false; attempt.Error ??= error; }
            try { writer?.Dispose(); }
            catch (Exception error) when (ManagedFailure(error)) { attempt.CleanupCompleted = false; attempt.Error ??= error; }
        }
        return attempt;
    }

    private GameEnrichmentResult PublishCommitted(WriteAttempt attempt)
    {
        var published = new List<CatalogMetadataSection>();
        var failed = new List<CatalogMetadataSection>();
        foreach (var result in attempt.Results)
        {
            try { publisher.Publish(attempt.Observations[result.Section]); published.Add(result.Section); }
            catch (Exception error) when (ManagedFailure(error)) { failed.Add(result.Section); }
        }
        var applied = attempt.Results.Count(r => r.Outcome == TaxonomyEnrichmentOutcome.Applied);
        var incomplete = attempt.Decisions.Any(d => d.Disposition is GameEnrichmentDisposition.DiscardedAfterRevalidation or GameEnrichmentDisposition.NotRequested);
        var outcome = failed.Count > 0 ? GameEnrichmentOutcome.CommittedWithPublicationFailure :
            attempt.Error is not null || !attempt.CleanupCompleted ? GameEnrichmentOutcome.CommittedWithPostCommitFailure :
            applied == attempt.Results.Count && !incomplete ? GameEnrichmentOutcome.Applied : applied > 0 ? GameEnrichmentOutcome.Partial : GameEnrichmentOutcome.Preserved;
        return Batch(outcome, attempt.Results, attempt.Decisions.Concat(attempt.Results.Select(r => new GameEnrichmentSectionDecision(r.Section, Disposition(r))))) with
        {
            CommitState = EnrichmentCommitState.Confirmed, CleanupCompleted = attempt.CleanupCompleted,
            PublicationState = failed.Count == 0 ? EnrichmentPublicationState.Complete : published.Count == 0 ? EnrichmentPublicationState.Failed : EnrichmentPublicationState.Partial,
            PublishedSections = Array.AsReadOnly(published.ToArray()), PublicationFailedSections = Array.AsReadOnly(failed.ToArray())
        };
    }

    private static bool ManagedFailure(Exception error) => EnrichmentExceptionPolicy.IsManaged(error);

    private static EnrichmentWriteFailure ClassifySqlite(Exception? error)
    {
        var sqlite = error as SqliteException ?? (error as DbUpdateException)?.InnerException as SqliteException;
        if (sqlite is null) return EnrichmentWriteFailure.Unknown;
        var extended = sqlite.SqliteExtendedErrorCode;
        if (sqlite.SqliteErrorCode == SQLITE_BUSY && extended is SQLITE_BUSY or SQLITE_BUSY_RECOVERY or SQLITE_BUSY_SNAPSHOT)
            return EnrichmentWriteFailure.Busy;
        if (sqlite.SqliteErrorCode == SQLITE_LOCKED && extended is SQLITE_LOCKED or SQLITE_LOCKED_SHAREDCACHE)
            return EnrichmentWriteFailure.Locked;
        if (sqlite.SqliteErrorCode != SQLITE_CONSTRAINT) return EnrichmentWriteFailure.Unknown;
        return extended switch
        {
            SQLITE_CONSTRAINT_FOREIGNKEY => EnrichmentWriteFailure.ForeignKey,
            SQLITE_CONSTRAINT_CHECK => EnrichmentWriteFailure.Check,
            SQLITE_CONSTRAINT_UNIQUE or SQLITE_CONSTRAINT_PRIMARYKEY => EnrichmentWriteFailure.Unique,
            _ => EnrichmentWriteFailure.Constraint
        };
    }
}

// Shared by recovery and diagnostic instrumentation: one definition of fatal exceptions.
internal static class EnrichmentExceptionPolicy
{
    internal static bool IsManaged(Exception error)
    {
        var pending = new Stack<Exception>();
        var visited = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        pending.Push(error);
        while (pending.TryPop(out var current))
        {
            if (!visited.Add(current)) continue;
            if (current is OutOfMemoryException or StackOverflowException or AccessViolationException) return false;
            if (current is AggregateException aggregate)
                foreach (var child in aggregate.InnerExceptions) pending.Push(child);
            if (current.InnerException is not null) pending.Push(current.InnerException);
        }
        return true;
    }
}
