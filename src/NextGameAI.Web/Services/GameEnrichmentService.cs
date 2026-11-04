using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

// Explicit invocation only. Both public operations share the same coordinated core.
public sealed partial class GameEnrichmentService(IServiceScopeFactory scopes, IIgdbMetadataClient metadata,
    TimeProvider clock, GameEnrichmentCoordinator coordinator, GameEnrichmentPolicy policy,
    GameEnrichmentCooldown cooldown, IGameEnrichmentPublisher? publication = null) : IGameEnrichmentService
{
    private readonly IGameEnrichmentPublisher publisher = publication ?? new GameEnrichmentPublisher(cooldown);
    public Task<GameEnrichmentResult> EnrichTaxonomiesAsync(int gameId, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(gameId, TaxonomySections, cancellationToken);

    public Task<GameEnrichmentResult> EnrichDurationAndMultiplayerAsync(int gameId, CancellationToken cancellationToken = default) =>
        EnrichCoreAsync(gameId, DetailSections, cancellationToken);

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
        return new(kind, rows, new(section, outcome, source.Status, source.Failure, source.StatusCode, source.RetryAfter));
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
        IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Taxonomy() => new(status, Failure: error.Failure, StatusCode: error.StatusCode, RetryAfter: error.RetryAfter);
        return new(id, Taxonomy(), Taxonomy(), Taxonomy(), Taxonomy(), Taxonomy(),
            new(status, Failure: error.Failure, StatusCode: error.StatusCode, RetryAfter: error.RetryAfter),
            new(status, Failure: error.Failure, StatusCode: error.StatusCode, RetryAfter: error.RetryAfter));
    }
}
