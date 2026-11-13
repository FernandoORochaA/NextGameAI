using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;

namespace NextGameAI.Web.Services;

public sealed class GameMetadataReadService(NextGameAiDbContext db, GameEnrichmentPolicy policy)
{
    private static readonly CatalogMetadataSection[] VisibleSections =
        [CatalogMetadataSection.Genres, CatalogMetadataSection.Themes, CatalogMetadataSection.GameModes,
         CatalogMetadataSection.PlayerPerspectives, CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer];

    // Null denotes a missing game; an existing game always gets a snapshot, even without metadata.
    public async Task<GameMetadataDetails?> GetByGameIdAsync(int gameId, CancellationToken cancellationToken = default)
    {
        if (gameId <= 0) throw new ArgumentOutOfRangeException(nameof(gameId));
        cancellationToken.ThrowIfCancellationRequested();
        if (!await db.Games.AsNoTracking().AnyAsync(g => g.Id == gameId, cancellationToken)) return null;

        var taxonomies = await db.GameTaxonomies.AsNoTracking()
            .Where(t => t.GameId == gameId &&
                (t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre || t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Theme ||
                 t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.GameMode || t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.PlayerPerspective))
            .OrderBy(t => t.CatalogTaxonomy.Name).ThenBy(t => t.CatalogTaxonomy.IgdbId)
            .Select(t => new { t.CatalogTaxonomy.Kind, t.CatalogTaxonomy.IgdbId, t.CatalogTaxonomy.Name })
            .ToListAsync(cancellationToken);
        IReadOnlyList<GameMetadataTaxonomy> Values(CatalogTaxonomyKind kind) => Array.AsReadOnly(
            taxonomies.Where(t => t.Kind == kind).Select(t => new GameMetadataTaxonomy(t.IgdbId, t.Name)).ToArray());

        var duration = await db.GameTimeToBeats.AsNoTracking().Where(t => t.GameId == gameId)
            .Select(t => new GameMetadataDuration(t.IgdbId, t.HastilySeconds, t.NormallySeconds,
                t.CompletelySeconds, t.SubmissionCount, t.Source)).SingleOrDefaultAsync(cancellationToken);
        var multiplayer = await db.GameMultiplayerModes.AsNoTracking().Where(m => m.GameId == gameId)
            .OrderBy(m => m.CatalogPlatform == null ? null : m.CatalogPlatform.Name)
            .ThenBy(m => m.CatalogPlatform == null ? (long?)null : m.CatalogPlatform.IgdbId).ThenBy(m => m.Id)
            .Select(m => new GameMetadataMultiplayer(m.IgdbId,
                m.CatalogPlatform == null ? null : new GameMetadataPlatform(m.CatalogPlatform.IgdbId, m.CatalogPlatform.Name),
                m.CampaignCoop, m.DropIn, m.LanCoop, m.OfflineCoop, m.OnlineCoop, m.SplitScreen,
                m.SplitScreenOnline, m.OfflineCoopMax, m.OfflineMax, m.OnlineCoopMax, m.OnlineMax, m.Source))
            .ToListAsync(cancellationToken);
        var states = await db.GameEnrichmentStates.AsNoTracking()
            .Where(s => s.GameId == gameId && VisibleSections.Contains(s.Section))
            .Select(s => new { s.Section, s.LastResult, s.LastAttemptAtUtc, s.LastSuccessAtUtc, s.Source })
            .ToListAsync(cancellationToken);
        var sectionStates = VisibleSections.Select(section =>
        {
            var stored = states.SingleOrDefault(s => s.Section == section);
            // Policy reuse only: no cooldown or decision about starting a query is exposed.
            var freshness = policy.Evaluate(section, stored is null ? null : new GameEnrichmentState
            {
                GameId = gameId, Section = section, LastResult = stored.LastResult,
                LastAttemptAtUtc = stored.LastAttemptAtUtc, LastSuccessAtUtc = stored.LastSuccessAtUtc, Source = stored.Source
            });
            return new GameMetadataSectionState(section, stored?.LastResult, Utc(stored?.LastAttemptAtUtc),
                Utc(stored?.LastSuccessAtUtc), stored?.Source, freshness.Freshness, freshness.ExpiresAtUtc);
        }).ToArray();
        return new(gameId, Values(CatalogTaxonomyKind.Genre), Values(CatalogTaxonomyKind.Theme),
            Values(CatalogTaxonomyKind.GameMode), Values(CatalogTaxonomyKind.PlayerPerspective), duration,
            Array.AsReadOnly(multiplayer.ToArray()), Array.AsReadOnly(sectionStates));
    }

    // SQLite materializes UTC-convention timestamps as Unspecified; preserve their ticks.
    private static DateTime? Utc(DateTime? value) => value.HasValue
        ? DateTime.SpecifyKind(value.Value, DateTimeKind.Utc) : null;
}
