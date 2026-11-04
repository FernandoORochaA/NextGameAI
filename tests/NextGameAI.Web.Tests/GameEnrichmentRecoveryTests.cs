using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;
using static SQLitePCL.raw;

namespace NextGameAI.Web.Tests;

// Same protected fixture; recovery cases are separated to keep the integration file reviewable.
public sealed partial class GameEnrichmentIntegrationTests
{
    private static Exception ExceptionStructure(string shape)
    {
        var common = new InvalidOperationException("Synthetic ordinary failure");
        var fatal = new OutOfMemoryException("Synthetic object only, no memory exhaustion");
        return shape switch
        {
            "common" => common,
            "fatal" => fatal,
            "stack" => new StackOverflowException("Synthetic object, no recursion"),
            "access" => new AccessViolationException("Synthetic object, no memory access"),
            "inner" => new InvalidOperationException("Wrapper", fatal),
            "first" => new AggregateException(fatal, common),
            "last" => new AggregateException(common, fatal),
            "middle" => new AggregateException(common, fatal, new ArgumentException("Synthetic")),
            "nested" => new AggregateException(common, new AggregateException(common, fatal)),
            "wrapper-aggregate" => new InvalidOperationException("Wrapper", new AggregateException(common, fatal)),
            "aggregate-wrapper" => new AggregateException(common, new InvalidOperationException("Wrapper", fatal)),
            "ordinary-aggregate" => new AggregateException(common, new ArgumentException("Synthetic")),
            "repeated" => new AggregateException(common, new AggregateException(common, common)),
            "repeated-fatal" => new AggregateException(common, new AggregateException(common, fatal), common, fatal),
            _ => throw new ArgumentOutOfRangeException(nameof(shape))
        };
    }

    [Theory]
    [InlineData("common", true)] [InlineData("ordinary-aggregate", true)] [InlineData("repeated", true)]
    [InlineData("fatal", false)] [InlineData("stack", false)] [InlineData("access", false)]
    [InlineData("inner", false)] [InlineData("first", false)] [InlineData("last", false)] [InlineData("middle", false)]
    [InlineData("nested", false)] [InlineData("wrapper-aggregate", false)] [InlineData("aggregate-wrapper", false)] [InlineData("repeated-fatal", false)]
    public void FatalClassificationVisitsAllAggregateBranchesAndRepeatedReferences(string shape, bool managed)
    {
        var method = typeof(GameEnrichmentService).GetMethod("ManagedFailure", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        Assert.Equal(managed, (bool)method.Invoke(null, new object[] { ExceptionStructure(shape) })!);
    }

    [Theory]
    [InlineData("first", "save")] [InlineData("last", "commit")] [InlineData("middle", "publication")]
    [InlineData("nested", "save")] [InlineData("wrapper-aggregate", "commit")] [InlineData("aggregate-wrapper", "publication")]
    public async Task FatalInAnyBranchPropagatesWithoutRetryOrRecoveryOutcome(string shape, string stage)
    {
        using var f = new Fixture(); var id = await f.Game(); var fault = ExceptionStructure(shape); var timers = 0;
        f.Clock.TimerScheduled = () => timers++;
        if (stage == "save") f.Save.BeforeSave = (_, _) => throw fault;
        if (stage == "commit") f.Commit.BeforeCommit = _ => throw fault;
        if (stage == "publication") f.Publication.BeforePublish = _ => throw fault;
        var observed = await Record.ExceptionAsync(() => f.Service().EnrichTaxonomiesAsync(id));
        Assert.Same(fault, observed); Assert.Equal(0, timers); Assert.Single(f.Metadata.Queries);
        Assert.Equal(0, f.Cooldown.Count); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
        using var db = f.Db(); Assert.Equal(stage == "publication" ? 5 : 0, await db.GameEnrichmentStates.CountAsync());
    }

    [Theory]
    [InlineData("save", GameEnrichmentOutcome.PersistenceFailure)]
    [InlineData("commit", GameEnrichmentOutcome.CommitIndeterminate)]
    [InlineData("publication", GameEnrichmentOutcome.CommittedWithPublicationFailure)]
    [InlineData("cleanup", GameEnrichmentOutcome.CommittedWithPostCommitFailure)]
    public async Task AggregatesContainingOnlyCommonFailuresKeepStageSpecificOutcomes(string stage, GameEnrichmentOutcome expected)
    {
        using var f = new Fixture(); var id = await f.Game(); var fault = ExceptionStructure("ordinary-aggregate"); var committed = false;
        if (stage == "save") f.Save.BeforeSave = (_, _) => throw fault;
        if (stage == "commit") f.Commit.BeforeCommit = _ => throw fault;
        if (stage == "publication") f.Publication.BeforePublish = _ => throw fault;
        if (stage == "cleanup")
        { f.Commit.AfterCommit = () => committed = true; f.ScopeDisposed = () => { if (committed) throw fault; }; }
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(expected, result.Outcome); Assert.Equal(1, result.WriteAttempts); Assert.Single(f.Metadata.Queries);
        Assert.Equal(0, f.Coordinator.RegisteredGameCount);
        if (stage is "publication" or "cleanup") Assert.Equal(EnrichmentCommitState.Confirmed, result.CommitState);
    }
    [Theory]
    [InlineData(SQLITE_BUSY, SQLITE_BUSY)]
    [InlineData(SQLITE_BUSY, SQLITE_BUSY_RECOVERY)]
    [InlineData(SQLITE_BUSY, SQLITE_BUSY_SNAPSHOT)]
    [InlineData(SQLITE_LOCKED, SQLITE_LOCKED)]
    [InlineData(SQLITE_LOCKED, SQLITE_LOCKED_SHAREDCACHE)]
    public async Task RecognizedPreCommitContentionRetriesOnceWithFreshContext(int primary, int extended)
    {
        using var f = new Fixture(); var id = await f.Game(); var delay = Signal(); f.Clock.TimerScheduled = () => delay.TrySetResult();
        var contexts = new List<Guid>();
        f.Save.BeforeSave = (db, _) =>
        {
            contexts.Add(db.ContextId.InstanceId); Assert.Equal(0, f.Cooldown.Count);
            if (contexts.Count == 1) throw new DbUpdateException("Synthetic", new SqliteException("No classification by message", primary, extended));
            return Task.CompletedTask;
        };
        var operation = f.Service().EnrichTaxonomiesAsync(id); await delay.Task.WaitAsync(Deadline);
        Assert.Single(contexts); Assert.Empty(f.Publication.Calls); Assert.Equal(0, f.Cooldown.Count);
        using (var other = await f.Coordinator.AcquireGameAsync(999))
        using (await other.AcquireWriteAsync().WaitAsync(Deadline))
        { using var db = f.Db(); Assert.Empty(await db.GameEnrichmentStates.ToArrayAsync()); }
        f.Clock.Advance(TimeSpan.FromMilliseconds(199)); Assert.False(operation.IsCompleted);
        f.Clock.Advance(TimeSpan.FromMilliseconds(1)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome); Assert.Equal(2, result.WriteAttempts);
        Assert.Equal(EnrichmentCommitState.Confirmed, result.CommitState); Assert.Equal(EnrichmentWriteFailure.None, result.WriteFailure);
        Assert.Equal(2, contexts.Distinct().Count()); Assert.Single(f.Metadata.Queries); Assert.True(result.CleanupCompleted);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FailedSaveAfterSqlRollsBackAllRowsBeforeSuccessfulRetry(bool details)
    {
        using var f = new Fixture(); var id = await f.Game(); await Run(f.Service(), id, details); f.Clock.Advance(TimeSpan.FromDays(30));
        var before = await f.Snapshot(); var delay = Signal(); f.Clock.TimerScheduled = () => delay.TrySetResult();
        f.Metadata.Handler = (external, _, _) => Task.FromResult(Good(external) with
        {
            Genres = new(IgdbMetadataStatus.Success, new[] { new IgdbTaxonomyResult(IgdbTaxonomyKind.Genre, 20, "Synthetic changed") }),
            TimeToBeat = new(IgdbMetadataStatus.Success, new(external, external, 0, 999, null, 0)),
            Multiplayer = new(IgdbMetadataStatus.Success, Good(external).Multiplayer.Value!.Select(m => m with { IgdbId = m.IgdbId + 1, Platform = new(48, "Synthetic PS") }).ToArray())
        });
        var saves = 0;
        f.Save.AfterSave = (_, _) => { if (Interlocked.Increment(ref saves) == 1) throw new SqliteException("Synthetic after SQL", SQLITE_BUSY); return Task.CompletedTask; };
        var operation = Run(f.Service(), id, details); await delay.Task.WaitAsync(Deadline);
        Assert.Equal(before, await f.Snapshot());
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome); Assert.Equal(2, result.WriteAttempts);
        using var read = f.Db();
        if (details) { Assert.Equal(999, (await read.GameTimeToBeats.SingleAsync()).NormallySeconds); Assert.Equal(2, await read.CatalogPlatforms.CountAsync()); }
        else Assert.True(await read.GameTaxonomies.AnyAsync(t => t.CatalogTaxonomy.IgdbId == 20));
        Assert.Equal(2, f.Metadata.Queries.Count); // Initial seed plus exactly one new external request.
    }

    [Theory]
    [InlineData(SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_FOREIGNKEY, EnrichmentWriteFailure.ForeignKey)]
    [InlineData(SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_CHECK, EnrichmentWriteFailure.Check)]
    [InlineData(SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_UNIQUE, EnrichmentWriteFailure.Unique)]
    [InlineData(SQLITE_CONSTRAINT, SQLITE_CONSTRAINT_PRIMARYKEY, EnrichmentWriteFailure.Unique)]
    [InlineData(SQLITE_CONSTRAINT, SQLITE_CONSTRAINT, EnrichmentWriteFailure.Constraint)]
    [InlineData(SQLITE_IOERR, SQLITE_IOERR, EnrichmentWriteFailure.Unknown)]
    [InlineData(SQLITE_BUSY, 999, EnrichmentWriteFailure.Unknown)]
    public async Task UnprovenOrPermanentSqliteErrorNeverRetries(int primary, int extended, EnrichmentWriteFailure expected)
    {
        using var f = new Fixture(); var id = await f.Game(); var calls = 0; var timers = 0;
        f.Clock.TimerScheduled = () => timers++;
        f.Save.BeforeSave = (_, _) => { calls++; throw new SqliteException("Message deliberately irrelevant", primary, extended); };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, result.Outcome); Assert.Equal(expected, result.WriteFailure);
        Assert.Equal(1, result.WriteAttempts); Assert.Equal(1, calls); Assert.Equal(0, timers);
        Assert.Empty(f.Publication.Calls); Assert.Single(f.Metadata.Queries); Assert.True(result.CleanupCompleted);
    }

    [Fact]
    public async Task UnknownManagedLogicFailureIsNotRetried()
    {
        using var f = new Fixture(); var id = await f.Game(); var calls = 0;
        f.Save.BeforeSave = (_, _) => { calls++; throw new InvalidOperationException("Synthetic logic failure"); };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(EnrichmentWriteFailure.Unknown, result.WriteFailure); Assert.Equal(1, result.WriteAttempts); Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SecondContentionFailureEndsWithOnlyOneMinutePersistenceCooldown()
    {
        using var f = new Fixture(); var id = await f.Game(); var before = await f.Snapshot(); var delay = Signal();
        f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Save.BeforeSave = (_, _) => { saves++; throw new SqliteException("Synthetic", SQLITE_BUSY); };
        var operation = f.Service().EnrichDurationAndMultiplayerAsync(id); await delay.Task.WaitAsync(Deadline);
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, result.Outcome); Assert.Equal(2, saves); Assert.Equal(2, result.WriteAttempts);
        Assert.Equal(before, await f.Snapshot()); Assert.Empty(f.Publication.Calls); Assert.Single(f.Metadata.Queries);
        var blocked = await f.Service().EnrichDurationAndMultiplayerAsync(id);
        Assert.All(blocked.Decisions, d => Assert.Equal(GameEnrichmentDisposition.CooldownActive, d.Disposition));
        Assert.Equal(f.Clock.GetUtcNow().UtcDateTime.AddMinutes(1), blocked.Decisions[0].NextAllowedAtUtc);
        Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetryReloadsIdentityAndStateAndNeverOverwritesChangesDuringDelay(bool identityChanged)
    {
        using var f = new Fixture(); var id = await f.Game(); var delay = Signal(); f.Clock.TimerScheduled = () => delay.TrySetResult();
        var saves = 0;
        f.Save.BeforeSave = (_, _) => { if (++saves == 1) throw new SqliteException("Synthetic", SQLITE_BUSY); return Task.CompletedTask; };
        var operation = f.Service().EnrichTaxonomiesAsync(id); await delay.Task.WaitAsync(Deadline);
        using (var db = f.Db())
        {
            if (identityChanged) (await db.Games.SingleAsync()).IgdbId = 132181;
            else db.GameEnrichmentStates.Add(new() { GameId = id, Section = CatalogMetadataSection.Genres,
                LastAttemptAtUtc = f.Clock.GetUtcNow().UtcDateTime, LastSuccessAtUtc = f.Clock.GetUtcNow().UtcDateTime, LastResult = CatalogUpdateResult.Success });
            await db.SaveChangesAsync();
        }
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(identityChanged ? GameEnrichmentOutcome.IdentityMismatch : GameEnrichmentOutcome.Partial, result.Outcome);
        Assert.Equal(2, result.WriteAttempts); Assert.Single(f.Metadata.Queries);
        Assert.DoesNotContain(f.Publication.Calls, o => o.Identity.Section == CatalogMetadataSection.Genres);
        using var read = f.Db(); Assert.False(await read.GameTaxonomies.AnyAsync(t => t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre));
    }

    [Fact]
    public async Task RealSharedMemoryWriterContentionRecoversWithDistinctConnection()
    {
        using var f = new Fixture(); var id = await f.Game(); var http = Signal(); var releaseHttp = Signal(); var delay = Signal();
        f.Clock.TimerScheduled = () => delay.TrySetResult();
        f.Metadata.Handler = async (external, _, token) => { http.SetResult(); await releaseHttp.Task.WaitAsync(token); return Good(external); };
        var operation = f.Service().EnrichTaxonomiesAsync(id); await http.Task.WaitAsync(Deadline);
        using var competing = f.Db(); await using var transaction = await competing.Database.BeginTransactionAsync();
        await competing.Database.ExecuteSqlRawAsync("UPDATE CatalogPlatforms SET Name=Name;");
        releaseHttp.SetResult(); await delay.Task.WaitAsync(Deadline);
        await transaction.RollbackAsync();
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome); Assert.Equal(2, result.WriteAttempts);
        Assert.Single(f.Metadata.Queries); Assert.True(result.CleanupCompleted);
    }

    [Theory]
    [InlineData("before-logic", false)] [InlineData("before-sqlite", false)]
    [InlineData("after-logic", true)] [InlineData("before-cancel", false)] [InlineData("after-cancel", true)]
    public async Task AnyRelevantManagedCommitFailureIsIndeterminateEvenWhenSqliteAlreadyCommitted(string fault, bool rowsCommitted)
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource(); var timers = 0;
        f.Clock.TimerScheduled = () => timers++;
        if (fault == "before-logic") f.Commit.BeforeCommit = _ => throw new InvalidOperationException("Synthetic commit failure");
        if (fault == "before-sqlite") f.Commit.BeforeCommit = _ => throw new SqliteException("Synthetic commit failure", SQLITE_BUSY);
        if (fault == "after-logic") f.Commit.AfterCommit = () => throw new InvalidOperationException("Defensive fault after SQLite commit, before caller observes return");
        if (fault == "before-cancel") f.Commit.BeforeCommit = _ => { ct.Cancel(); return Task.FromCanceled(ct.Token); };
        if (fault == "after-cancel") f.Commit.AfterCommit = () => { ct.Cancel(); throw new OperationCanceledException(ct.Token); };
        var result = await f.Service().EnrichTaxonomiesAsync(id, ct.Token);
        Assert.Equal(GameEnrichmentOutcome.CommitIndeterminate, result.Outcome); Assert.Equal(EnrichmentCommitState.Indeterminate, result.CommitState);
        Assert.Equal(1, result.WriteAttempts); Assert.Empty(f.Publication.Calls); Assert.Equal(0, f.Cooldown.Count); Assert.Equal(0, timers);
        Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
        using var db = f.Db(); Assert.Equal(rowsCommitted ? 5 : 0, await db.GameEnrichmentStates.CountAsync());
    }

    [Theory]
    [InlineData("all", 0)] [InlineData("one", 4)] [InlineData("cancel", 4)]
    public async Task ConfirmedCommitWithPublicationFailureKeepsDurableResultsAndReportsPartialCache(string fault, int published)
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Publication.BeforePublish = observation =>
        {
            if (fault == "all" || observation.Identity.Section == CatalogMetadataSection.Themes)
            { if (fault == "cancel") throw new OperationCanceledException("Synthetic publication cancellation"); throw new ArgumentException("Synthetic publication failure"); }
        };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.CommittedWithPublicationFailure, result.Outcome); Assert.Equal(EnrichmentCommitState.Confirmed, result.CommitState);
        Assert.Equal(published == 0 ? EnrichmentPublicationState.Failed : EnrichmentPublicationState.Partial, result.PublicationState);
        Assert.Equal(published, result.PublishedSections.Count); Assert.Equal(5 - published, result.PublicationFailedSections.Count);
        Assert.Equal(5, result.Sections.Count); Assert.Equal(1, result.WriteAttempts); Assert.Equal(EnrichmentWriteFailure.None, result.WriteFailure);
        using (var db = f.Db()) Assert.All(await db.GameEnrichmentStates.ToArrayAsync(), s => Assert.Equal(CatalogUpdateResult.Success, s.LastResult));
        await f.Service().EnrichTaxonomiesAsync(id); Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task FailedRollbackPreventsRetryEvenForRecognizedBusy()
    {
        using var f = new Fixture(); var id = await f.Game(); var saves = 0; var timers = 0;
        f.Clock.TimerScheduled = () => timers++;
        f.Save.BeforeSave = (_, _) => { saves++; throw new SqliteException("Synthetic busy", SQLITE_BUSY); };
        f.Commit.BeforeRollback = () => throw new InvalidOperationException("Synthetic rollback failure");
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, result.Outcome); Assert.False(result.CleanupCompleted);
        Assert.Equal(1, result.WriteAttempts); Assert.Equal(1, saves); Assert.Equal(0, timers); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task CancellationDuringRetryDelayNeverCreatesAnotherWriteOrCooldown()
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource(); var delay = Signal();
        f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Save.BeforeSave = (_, _) => { saves++; throw new SqliteException("Synthetic", SQLITE_BUSY); };
        var operation = f.Service().EnrichTaxonomiesAsync(id, ct.Token); await delay.Task.WaitAsync(Deadline); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Deadline));
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); Assert.Equal(1, saves); Assert.Equal(0, f.Cooldown.Count);
        Assert.Empty(f.Publication.Calls); Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancellationAfterSqlInFirstOrSecondWriteRollsBackWithoutFailureCooldown(bool second)
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource(); var delay = Signal();
        f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Save.BeforeSave = (_, _) => { if (++saves == 1 && second) throw new SqliteException("Synthetic", SQLITE_BUSY); return Task.CompletedTask; };
        f.Save.AfterSave = (_, token) => { ct.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
        var operation = f.Service().EnrichDurationAndMultiplayerAsync(id, ct.Token);
        if (second) { await delay.Task.WaitAsync(Deadline); f.Clock.Advance(TimeSpan.FromMilliseconds(200)); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation.WaitAsync(Deadline));
        Assert.Equal(second ? 2 : 1, saves); Assert.Equal(0, f.Cooldown.Count); Assert.Empty(f.Publication.Calls);
        using var db = f.Db(); Assert.Empty(await db.GameTimeToBeats.ToArrayAsync()); Assert.Empty(await db.CatalogPlatforms.ToArrayAsync());
    }

    [Fact]
    public async Task CancellationAfterContentionBeforeDelayDoesNotScheduleRetry()
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource(); var timers = 0;
        f.Clock.TimerScheduled = () => timers++;
        f.Save.BeforeSave = (_, _) => { ct.Cancel(); throw new SqliteException("Synthetic", SQLITE_BUSY); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service().EnrichTaxonomiesAsync(id, ct.Token));
        Assert.Equal(0, timers); Assert.Equal(0, f.Cooldown.Count); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task FailureSchedulingDelayStopsWithoutSecondWriteOrHttp()
    {
        using var f = new Fixture(); var id = await f.Game(); f.Clock.FailScheduling = true;
        f.Save.BeforeSave = (_, _) => throw new SqliteException("Synthetic", SQLITE_BUSY);
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, result.Outcome); Assert.Equal(EnrichmentWriteFailure.Unknown, result.WriteFailure);
        Assert.Equal(1, result.WriteAttempts); Assert.Single(f.Metadata.Queries); Assert.Empty(f.Publication.Calls);
    }

    [Fact]
    public async Task WriteRetryDoesNotRecalculateRetryAfterOrRequeryMetadata()
    {
        using var f = new Fixture(); var id = await f.Game(); var start = f.Clock.GetUtcNow().UtcDateTime; var delay = Signal();
        f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Metadata.Handler = (external, _, _) =>
        {
            f.Clock.Advance(TimeSpan.FromMinutes(10));
            return Task.FromResult(Good(external) with { Genres = new(IgdbMetadataStatus.TransportFailure,
                Failure: IgdbQueryFailure.RateLimited, StatusCode: System.Net.HttpStatusCode.TooManyRequests, RetryAfter: TimeSpan.FromMinutes(40)) });
        };
        f.Save.BeforeSave = (_, _) => { if (++saves == 1) throw new SqliteException("Synthetic", SQLITE_BUSY); return Task.CompletedTask; };
        var operation = f.Service().EnrichTaxonomiesAsync(id); await delay.Task.WaitAsync(Deadline);
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        using var db = f.Db(); var state = await db.GameEnrichmentStates.AsNoTracking().SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        Assert.Equal(start.AddMinutes(50), f.Cooldown.Evaluate(id, state.Section, state).NextAllowedAtUtc);
        Assert.Equal(start.AddMinutes(10), state.LastAttemptAtUtc); Assert.Equal(2, result.WriteAttempts); Assert.Single(f.Metadata.Queries);
        Assert.All(f.Publication.Calls, o => Assert.Equal(start.AddMinutes(10), o.ReferenceAtUtc));
    }

    [Fact]
    public async Task FailureAfterCacheMutationReportsUncertainPublicationWithoutUndoingCommit()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Publication.AfterPublish = observation =>
        { if (observation.Identity.Section == CatalogMetadataSection.Genres) throw new InvalidOperationException("Synthetic failure after cache mutation"); };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.CommittedWithPublicationFailure, result.Outcome);
        Assert.Equal(EnrichmentCommitState.Confirmed, result.CommitState);
        Assert.Equal(CatalogMetadataSection.Genres, Assert.Single(result.PublicationFailedSections));
        Assert.Equal(4, result.PublishedSections.Count); Assert.Equal(5, f.Cooldown.Count);
        using var db = f.Db(); Assert.Equal(5, await db.GameEnrichmentStates.CountAsync()); Assert.Single(f.Metadata.Queries);
    }

    [Fact]
    public async Task ScopedCleanupFailureAfterConfirmedCommitIsNotPersistenceFailure()
    {
        using var f = new Fixture(); var id = await f.Game(); var committed = false;
        f.Commit.AfterCommit = () => committed = true;
        f.ScopeDisposed = () => { if (committed) throw new InvalidOperationException("Defensive post-commit scope cleanup fault"); };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.CommittedWithPostCommitFailure, result.Outcome);
        Assert.Equal(EnrichmentCommitState.Confirmed, result.CommitState); Assert.False(result.CleanupCompleted);
        Assert.Equal(EnrichmentPublicationState.Complete, result.PublicationState); Assert.Equal(5, f.Cooldown.Count);
        Assert.Equal(0, f.Coordinator.RegisteredGameCount); Assert.Single(f.Metadata.Queries);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task FatalPlatformExceptionIsNotConvertedToARecoveryResult(bool wrapped)
    {
        using var f = new Fixture(); var id = await f.Game();
        // Defensive synthetic exception object, no actual resource exhaustion is caused.
        f.Commit.BeforeCommit = _ =>
        {
            if (wrapped) throw new DbUpdateException("Synthetic wrapper", new OutOfMemoryException("Synthetic fatal classification"));
            throw new OutOfMemoryException("Synthetic fatal classification");
        };
        if (wrapped) Assert.IsType<OutOfMemoryException>((await Assert.ThrowsAsync<DbUpdateException>(() => f.Service().EnrichTaxonomiesAsync(id))).InnerException);
        else await Assert.ThrowsAsync<OutOfMemoryException>(() => f.Service().EnrichTaxonomiesAsync(id));
        Assert.Empty(f.Publication.Calls); Assert.Equal(0, f.Cooldown.Count); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RetryReloadsSharedReferencesCreatedByAnotherTransaction(bool details)
    {
        using var f = new Fixture(); var id = await f.Game(); var delay = Signal(); f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Save.BeforeSave = (_, _) => { if (++saves == 1) throw new SqliteException("Synthetic", SQLITE_BUSY); return Task.CompletedTask; };
        var operation = Run(f.Service(), id, details); await delay.Task.WaitAsync(Deadline);
        using (var db = f.Db())
        {
            if (details) db.CatalogPlatforms.Add(new() { IgdbId = 6, Name = "Created elsewhere" });
            else db.CatalogTaxonomies.Add(new() { Kind = CatalogTaxonomyKind.Genre, IgdbId = 10, Name = "Created elsewhere" });
            await db.SaveChangesAsync();
        }
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome); Assert.Equal(2, result.WriteAttempts); Assert.Single(f.Metadata.Queries);
        using var read = f.Db();
        Assert.Equal(details ? 1 : 0, await read.CatalogPlatforms.CountAsync()); Assert.Equal(details ? 0 : 5, await read.CatalogTaxonomies.CountAsync());
    }

    [Fact]
    public async Task RetryDiscardsAllStatesMadeRecentDuringDelayWithoutSavingAgain()
    {
        using var f = new Fixture(); var id = await f.Game(); var delay = Signal(); f.Clock.TimerScheduled = () => delay.TrySetResult(); var saves = 0;
        f.Save.BeforeSave = (_, _) => { saves++; throw new SqliteException("Synthetic", SQLITE_BUSY); };
        var operation = f.Service().EnrichDurationAndMultiplayerAsync(id); await delay.Task.WaitAsync(Deadline);
        using (var db = f.Db())
        {
            var now = f.Clock.GetUtcNow().UtcDateTime;
            foreach (var section in new[] { CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer })
                await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO GameEnrichmentStates (GameId, Section, LastAttemptAtUtc, LastSuccessAtUtc, LastResult, Source) VALUES ({id}, {(int)section}, {now}, {now}, {0}, {"IGDB"});");
        }
        f.Clock.Advance(TimeSpan.FromMilliseconds(200)); var result = await operation.WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Preserved, result.Outcome); Assert.Equal(2, result.WriteAttempts); Assert.Equal(1, saves);
        Assert.All(result.Decisions, d => Assert.Equal(GameEnrichmentDisposition.DiscardedAfterRevalidation, d.Disposition));
        Assert.Empty(f.Publication.Calls); Assert.Single(f.Metadata.Queries);
    }
}
