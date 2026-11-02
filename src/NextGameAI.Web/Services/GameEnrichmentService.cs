using System.ComponentModel.DataAnnotations;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

// Inert until explicitly called. Sequential idempotence only; coordination belongs to 4B.5.
public sealed partial class GameEnrichmentService(IServiceScopeFactory scopes, IIgdbMetadataClient metadata,
    TimeProvider clock) : IGameEnrichmentService
{
    public Task<GameEnrichmentResult> EnrichTaxonomiesAsync(int gameId, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(gameId, false, cancellationToken);

    public Task<GameEnrichmentResult> EnrichDurationAndMultiplayerAsync(int gameId, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(gameId, true, cancellationToken);

    private async Task<GameEnrichmentResult> EnrichCoreAsync(int gameId, bool details, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            long? igdbId;
            using (var scope = scopes.CreateScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
                var identity = await db.Games.AsNoTracking().Where(g => g.Id == gameId)
                    .Select(g => new { g.IgdbId }).SingleOrDefaultAsync(cancellationToken);
                if (identity is null) return Result(GameEnrichmentOutcome.GameNotFound);
                igdbId = identity.IgdbId;
            }
            if (igdbId is not > 0) return Result(GameEnrichmentOutcome.MissingIgdbId);

            // No transaction or tracked context crosses the external query.
            IgdbMetadataResult external;
            try { external = await metadata.GetByIdAsync(igdbId.Value, cancellationToken); }
            catch (IgdbQueryException error) { external = Failed(igdbId.Value, error); }
            if (external.IgdbId != igdbId) return Result(GameEnrichmentOutcome.IdentityMismatch);
            var plans = details ? new[] { PrepareTime(external.TimeToBeat, igdbId.Value), PrepareMultiplayer(external.Multiplayer, igdbId.Value) } : new[]
            {
                Prepare(CatalogMetadataSection.Genres, CatalogTaxonomyKind.Genre, IgdbTaxonomyKind.Genre, external.Genres),
                Prepare(CatalogMetadataSection.Themes, CatalogTaxonomyKind.Theme, IgdbTaxonomyKind.Theme, external.Themes),
                Prepare(CatalogMetadataSection.GameModes, CatalogTaxonomyKind.GameMode, IgdbTaxonomyKind.GameMode, external.GameModes),
                Prepare(CatalogMetadataSection.PlayerPerspectives, CatalogTaxonomyKind.PlayerPerspective, IgdbTaxonomyKind.PlayerPerspective, external.PlayerPerspectives),
                Prepare(CatalogMetadataSection.Keywords, CatalogTaxonomyKind.Keyword, IgdbTaxonomyKind.Keyword, external.Keywords)
            }.Select(plan => new Work(plan.Result, async (db, id, ct) =>
            {
                await Synchronize(db, id, plan, ct);
                return true;
            })).ToArray();
            plans = plans.Where(p => p.Result.ExternalStatus != IgdbMetadataStatus.NotRequested).ToArray();
            if (plans.Length == 0) return Result(GameEnrichmentOutcome.Preserved);
            var now = clock.GetUtcNow().UtcDateTime;
            using var writeScope = scopes.CreateScope();
            var write = writeScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            await using var transaction = await write.Database.BeginTransactionAsync(cancellationToken);
            if (!await Matches(write, gameId, igdbId.Value, cancellationToken))
                return Result(GameEnrichmentOutcome.IdentityMismatch);
            for (var index = 0; index < plans.Length; index++)
            {
                var plan = plans[index];
                if (plan.Result.Outcome == TaxonomyEnrichmentOutcome.Applied)
                {
                    if (!await plan.Persist!(write, gameId, cancellationToken))
                    {
                        plan = plan with { Result = plan.Result with { Outcome = TaxonomyEnrichmentOutcome.InvalidResponse } };
                        plans[index] = plan;
                    }
                }
                var state = await write.GameEnrichmentStates.SingleOrDefaultAsync(
                    s => s.GameId == gameId && s.Section == plan.Result.Section, cancellationToken);
                // Never fabricate timestamps or violate a newer persisted state when the clock moves back.
                if (state is not null && state.LastAttemptAtUtc > now)
                    return Result(GameEnrichmentOutcome.PersistenceFailure);
                if (state is null)
                {
                    state = new GameEnrichmentState { GameId = gameId, Section = plan.Result.Section };
                    write.GameEnrichmentStates.Add(state);
                }
                state.LastAttemptAtUtc = now;
                state.Source = "IGDB";
                state.LastResult = plan.Result.Outcome switch
                {
                    TaxonomyEnrichmentOutcome.Applied => CatalogUpdateResult.Success,
                    TaxonomyEnrichmentOutcome.GameNotFound => CatalogUpdateResult.NotFound,
                    _ => CatalogUpdateResult.TemporaryFailure
                };
                if (state.LastResult == CatalogUpdateResult.Success) state.LastSuccessAtUtc = now;
            }
            await write.SaveChangesAsync(cancellationToken);
            if (!await Matches(write, gameId, igdbId.Value, cancellationToken))
                return Result(GameEnrichmentOutcome.IdentityMismatch);
            await transaction.CommitAsync(cancellationToken);
            var applied = plans.Count(p => p.Result.Outcome == TaxonomyEnrichmentOutcome.Applied);
            return new(applied == plans.Length ? GameEnrichmentOutcome.Applied : applied > 0
                ? GameEnrichmentOutcome.Partial : GameEnrichmentOutcome.Preserved,
                Array.AsReadOnly(plans.Select(p => p.Result).ToArray()));
        }
        // Disposal rolls back the complete operation, including state writes. No raw exception escapes.
        catch (DbUpdateException) { return Result(GameEnrichmentOutcome.PersistenceFailure); }
        catch (DbException) { return Result(GameEnrichmentOutcome.PersistenceFailure); }
        catch (ValidationException) { return Result(GameEnrichmentOutcome.PersistenceFailure); }
    }

    private static Task<bool> Matches(NextGameAiDbContext db, int id, long externalId, CancellationToken ct) =>
        db.Games.AsNoTracking().AnyAsync(g => g.Id == id && g.IgdbId == externalId, ct);

    private static Plan Prepare(CatalogMetadataSection section, CatalogTaxonomyKind kind, IgdbTaxonomyKind externalKind,
        IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> source)
    {
        var outcome = source.Status switch
        {
            IgdbMetadataStatus.GameNotFound => TaxonomyEnrichmentOutcome.GameNotFound,
            IgdbMetadataStatus.TransportFailure => TaxonomyEnrichmentOutcome.ExternalFailure,
            _ => TaxonomyEnrichmentOutcome.InvalidResponse
        };
        IgdbTaxonomyResult[] rows = [];
        if (source.Status == IgdbMetadataStatus.Success && source.Value is { Count: > 0 } && source.Failure is null)
        {
            var snapshot = source.Value.ToArray();
            if (snapshot.All(r => r is not null && r.Kind == externalKind && r.IgdbId > 0 &&
                !string.IsNullOrWhiteSpace(r.Name) && r.Name.Length <= 500 && !r.Name.Any(char.IsControl)) &&
                snapshot.Select(r => r.IgdbId).Distinct().Count() == snapshot.Length)
            {
                rows = snapshot.Select(r => new IgdbTaxonomyResult(r.Kind, r.IgdbId, r.Name.Trim())).ToArray();
                outcome = TaxonomyEnrichmentOutcome.Applied;
            }
        }
        else if (source.Status == IgdbMetadataStatus.Empty && source.Value is { Count: 0 } && source.Failure is null)
        {
            // 4B.2 maps omitted fields and explicit empty lists to the same status. It cannot prove
            // a destructive empty snapshot. Keep old links and expose the limitation, not success.
            outcome = TaxonomyEnrichmentOutcome.EmptyPreserved;
        }
        if (source.Value is not null && source.Status is IgdbMetadataStatus.GameNotFound or IgdbMetadataStatus.TransportFailure)
            outcome = TaxonomyEnrichmentOutcome.InvalidResponse;
        return new(kind, rows, new(section, outcome, source.Status, source.Failure));
    }

    private static async Task Synchronize(NextGameAiDbContext db, int gameId, Plan plan, CancellationToken ct)
    {
        var current = await db.GameTaxonomies.Include(l => l.CatalogTaxonomy)
            .Where(l => l.GameId == gameId && l.CatalogTaxonomy.Kind == plan.Kind).ToListAsync(ct);
        var desired = plan.Rows.Select(r => r.IgdbId).ToHashSet();
        db.GameTaxonomies.RemoveRange(current.Where(l => !desired.Contains(l.CatalogTaxonomy.IgdbId)));
        foreach (var row in plan.Rows)
        {
            var taxonomy = await db.CatalogTaxonomies.SingleOrDefaultAsync(t => t.Kind == plan.Kind && t.IgdbId == row.IgdbId, ct);
            if (taxonomy is null)
            {
                taxonomy = new CatalogTaxonomy { Kind = plan.Kind, IgdbId = row.IgdbId, Name = row.Name };
                db.CatalogTaxonomies.Add(taxonomy);
            }
            else if (taxonomy.Name != row.Name) taxonomy.Name = row.Name;
            if (!current.Any(l => l.CatalogTaxonomy.IgdbId == row.IgdbId))
                db.GameTaxonomies.Add(new GameTaxonomy { GameId = gameId, CatalogTaxonomy = taxonomy });
        }
    }

    private static GameEnrichmentResult Result(GameEnrichmentOutcome outcome) => new(outcome, Array.Empty<TaxonomyEnrichmentResult>());
    private sealed record Plan(CatalogTaxonomyKind Kind, IgdbTaxonomyResult[] Rows, TaxonomyEnrichmentResult Result);
    private sealed record Work(TaxonomyEnrichmentResult Result,
        Func<NextGameAiDbContext, int, CancellationToken, Task<bool>>? Persist = null);

    private static IgdbMetadataResult Failed(long id, IgdbQueryException error)
    {
        var status = error.Failure == IgdbQueryFailure.InvalidResponse ? IgdbMetadataStatus.InvalidResponse : IgdbMetadataStatus.TransportFailure;
        IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Taxonomy() => new(status, Failure: error.Failure);
        return new(id, Taxonomy(), Taxonomy(), Taxonomy(), Taxonomy(), Taxonomy(),
            new(status, Failure: error.Failure), new(status, Failure: error.Failure));
    }
}
