using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

public sealed partial class GameEnrichmentService
{
    private static readonly CatalogMetadataSection[] TaxonomySections =
        [CatalogMetadataSection.Genres, CatalogMetadataSection.Themes, CatalogMetadataSection.GameModes,
         CatalogMetadataSection.PlayerPerspectives, CatalogMetadataSection.Keywords];
    private static readonly CatalogMetadataSection[] DetailSections =
        [CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer];

    private async Task<GameEnrichmentResult> EnrichCoreAsync(int gameId, IReadOnlyCollection<CatalogMetadataSection> requested,
        CancellationToken ct)
    {
        if (gameId <= 0) throw new ArgumentOutOfRangeException(nameof(gameId));
        if (requested is null || requested.Any(s => !Enum.IsDefined(s))) throw new ArgumentException("Seções inválidas.", nameof(requested));
        ct.ThrowIfCancellationRequested();
        using var gameLease = await coordinator.AcquireGameAsync(gameId, ct);
        var decisions = new List<GameEnrichmentSectionDecision>();
        var ids = new Dictionary<CatalogMetadataSection, EnrichmentAttemptId>();
        try
        {
            long? igdbId;
            Dictionary<CatalogMetadataSection, GameEnrichmentState> initial;
            using (var readScope = scopes.CreateScope())
            {
                var read = readScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
                var game = await read.Games.AsNoTracking().Where(g => g.Id == gameId).Select(g => new { g.IgdbId }).SingleOrDefaultAsync(ct);
                if (game is null) return Result(GameEnrichmentOutcome.GameNotFound);
                igdbId = game.IgdbId;
                if (igdbId is not > 0) return Result(GameEnrichmentOutcome.MissingIgdbId);
                initial = await read.GameEnrichmentStates.AsNoTracking().Where(s => s.GameId == gameId).ToDictionaryAsync(s => s.Section, ct);
            }
            var selected = new List<CatalogMetadataSection>();
            foreach (var section in requested.Distinct())
            {
                var blocked = Blocked(gameId, section, initial.GetValueOrDefault(section));
                if (blocked is not null) decisions.Add(blocked);
                else selected.Add(section);
            }
            if (selected.Count == 0) return Batch(GameEnrichmentOutcome.Preserved, [], decisions);
            foreach (var section in selected) ids.Add(section, gameLease.CreateAttempt(section));

            // No DbContext, transaction or global write lease survives across the client call.
            IgdbMetadataResult external;
            try { external = await metadata.GetSectionsAsync(igdbId.Value, selected.Select(ExternalSection).ToArray(), ct); }
            catch (IgdbQueryException error) { external = Failed(igdbId.Value, error); }
            var clientCompleted = clock.GetUtcNow().UtcDateTime;
            ct.ThrowIfCancellationRequested();
            if (external.IgdbId != igdbId) return Batch(GameEnrichmentOutcome.IdentityMismatch, [],
                decisions.Concat(selected.Select(s => new GameEnrichmentSectionDecision(s, GameEnrichmentDisposition.DiscardedAfterRevalidation))));
            var plans = selected.Select(s => PrepareWork(s, external)).ToArray();
            foreach (var p in plans.Where(p => p.Result.ExternalStatus == IgdbMetadataStatus.NotRequested))
                decisions.Add(new(p.Result.Section, GameEnrichmentDisposition.NotRequested));
            plans = plans.Where(p => p.Result.ExternalStatus != IgdbMetadataStatus.NotRequested).ToArray();
            if (plans.Length == 0) return Batch(GameEnrichmentOutcome.Preserved, [], decisions);

            // Freeze both the destined persisted timestamp and the post-client deadline before waiting.
            var observations = plans.ToDictionary(p => p.Result.Section,
                p => cooldown.PrepareResult(ids[p.Result.Section], clientCompleted, clientCompleted, p.Result)!);
            return await RecoverWriteAsync(gameId, igdbId.Value, gameLease, initial, plans, observations, ids, clientCompleted, decisions, ct);
        }
        catch (Exception error) when (ManagedFailure(error) && error is (DbUpdateException or DbException or ValidationException))
        {
            // Initial-read failure only: recovery classifies and returns its own write failures.
            return Result(GameEnrichmentOutcome.PersistenceFailure);
        }
    }

    private GameEnrichmentSectionDecision? Blocked(int gameId, CatalogMetadataSection section, GameEnrichmentState? state)
    {
        var fresh = policy.Evaluate(section, state);
        var decision = cooldown.Evaluate(gameId, section, state);
        if (fresh.Freshness == EnrichmentFreshness.InconsistentTimestamp || decision.Block == EnrichmentBlock.InconsistentTimestamp)
            return new(section, GameEnrichmentDisposition.TemporalInconsistency);
        if (decision.CooldownActive) return new(section, GameEnrichmentDisposition.CooldownActive, decision.NextAllowedAtUtc);
        if (!fresh.Eligible) return new(section, GameEnrichmentDisposition.Recent, fresh.ExpiresAtUtc);
        return null;
    }

    private static bool SameState(GameEnrichmentState? a, GameEnrichmentState? b) => a is null ? b is null :
        b is not null && a.LastAttemptAtUtc == b.LastAttemptAtUtc && a.LastSuccessAtUtc == b.LastSuccessAtUtc && a.LastResult == b.LastResult && a.Source == b.Source;

    private static GameEnrichmentResult Batch(GameEnrichmentOutcome outcome, IEnumerable<TaxonomyEnrichmentResult> results,
        IEnumerable<GameEnrichmentSectionDecision> decisions) => new(outcome, Array.AsReadOnly(results.ToArray()))
        { Decisions = Array.AsReadOnly(decisions.ToArray()) };

    private static GameEnrichmentDisposition Disposition(TaxonomyEnrichmentResult result) => result.Outcome switch
    {
        TaxonomyEnrichmentOutcome.Applied => GameEnrichmentDisposition.Updated,
        TaxonomyEnrichmentOutcome.EmptyPreserved => GameEnrichmentDisposition.EmptyPreserved,
        TaxonomyEnrichmentOutcome.GameNotFound => GameEnrichmentDisposition.GameNotFound,
        TaxonomyEnrichmentOutcome.InvalidResponse => GameEnrichmentDisposition.InvalidResponse,
        _ => GameEnrichmentDisposition.ExternalFailure
    };

    private static IgdbMetadataSectionKind ExternalSection(CatalogMetadataSection section) => section switch
    {
        CatalogMetadataSection.Genres => IgdbMetadataSectionKind.Genres,
        CatalogMetadataSection.Themes => IgdbMetadataSectionKind.Themes,
        CatalogMetadataSection.GameModes => IgdbMetadataSectionKind.GameModes,
        CatalogMetadataSection.PlayerPerspectives => IgdbMetadataSectionKind.PlayerPerspectives,
        CatalogMetadataSection.Keywords => IgdbMetadataSectionKind.Keywords,
        CatalogMetadataSection.TimeToBeat => IgdbMetadataSectionKind.TimeToBeat,
        CatalogMetadataSection.Multiplayer => IgdbMetadataSectionKind.Multiplayer,
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };

    private static Work PrepareWork(CatalogMetadataSection section, IgdbMetadataResult external) => section switch
    {
        CatalogMetadataSection.Genres => TaxonomyWork(Prepare(section, CatalogTaxonomyKind.Genre, IgdbTaxonomyKind.Genre, external.Genres)),
        CatalogMetadataSection.Themes => TaxonomyWork(Prepare(section, CatalogTaxonomyKind.Theme, IgdbTaxonomyKind.Theme, external.Themes)),
        CatalogMetadataSection.GameModes => TaxonomyWork(Prepare(section, CatalogTaxonomyKind.GameMode, IgdbTaxonomyKind.GameMode, external.GameModes)),
        CatalogMetadataSection.PlayerPerspectives => TaxonomyWork(Prepare(section, CatalogTaxonomyKind.PlayerPerspective, IgdbTaxonomyKind.PlayerPerspective, external.PlayerPerspectives)),
        CatalogMetadataSection.Keywords => TaxonomyWork(Prepare(section, CatalogTaxonomyKind.Keyword, IgdbTaxonomyKind.Keyword, external.Keywords)),
        CatalogMetadataSection.TimeToBeat => PrepareTime(external.TimeToBeat, external.IgdbId),
        CatalogMetadataSection.Multiplayer => PrepareMultiplayer(external.Multiplayer, external.IgdbId),
        _ => throw new ArgumentOutOfRangeException(nameof(section))
    };
    private static Work TaxonomyWork(Plan plan) => new(plan.Result, async (db, id, ct) => { await Synchronize(db, id, plan, ct); return true; });
}
