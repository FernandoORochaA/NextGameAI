using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Services;

public sealed partial class GameEnrichmentService
{
    private static Work PrepareTime(IgdbMetadataSection<IgdbTimeToBeatResult> source, long externalId)
    {
        var result = DetailResult(CatalogMetadataSection.TimeToBeat, source.Status, source.Failure,
            source.Status == IgdbMetadataStatus.Empty && source.Value is null) with { StatusCode = source.StatusCode, RetryAfter = source.RetryAfter };
        var row = source.Value;
        if (source.Status == IgdbMetadataStatus.Success && source.Failure is null && row is not null &&
            row.GameIgdbId == externalId && row.IgdbId > 0 && row.HastilySeconds is not < 0 &&
            row.NormallySeconds is not < 0 && row.CompletelySeconds is not < 0 && row.SubmissionCount is not < 0)
        {
            // An external record/count alone is not a duration estimate. Do not create an all-null
            // estimate or erase a previous one when the response contains no duration value.
            if (row.HastilySeconds is null && row.NormallySeconds is null && row.CompletelySeconds is null)
                return new(result with { Outcome = TaxonomyEnrichmentOutcome.EmptyPreserved });
            return new(result with { Outcome = TaxonomyEnrichmentOutcome.Applied }, (db, id, ct) => PersistTime(db, id, row, ct));
        }
        if (source.Value is not null) result = result with { Outcome = TaxonomyEnrichmentOutcome.InvalidResponse };
        return new(result);
    }

    private static Work PrepareMultiplayer(IgdbMetadataSection<IReadOnlyList<IgdbMultiplayerResult>> source, long externalId)
    {
        var result = DetailResult(CatalogMetadataSection.Multiplayer, source.Status, source.Failure,
            source.Status == IgdbMetadataStatus.Empty && source.Value is { Count: 0 }) with { StatusCode = source.StatusCode, RetryAfter = source.RetryAfter };
        if (source.Status == IgdbMetadataStatus.Success && source.Failure is null && source.Value is { Count: > 0 and < 100 })
        {
            var rows = source.Value.ToArray();
            var ids = new HashSet<long>();
            var platforms = new HashSet<long?>();
            if (rows.All(r => r is not null && r.GameIgdbId == externalId && r.IgdbId > 0 && ids.Add(r.IgdbId) &&
                r.OfflineCoopMax is not < 0 && r.OfflineMax is not < 0 && r.OnlineCoopMax is not < 0 && r.OnlineMax is not < 0 &&
                (r.Platform is null || r.Platform.IgdbId > 0 && ValidPlatformName(r.Platform.Name)) && platforms.Add(r.Platform?.IgdbId)))
                return new(result with { Outcome = TaxonomyEnrichmentOutcome.Applied }, (db, id, ct) => PersistMultiplayer(db, id, rows, ct));
        }
        if (source.Value is not null && !(source.Status == IgdbMetadataStatus.Empty && source.Value.Count == 0))
            result = result with { Outcome = TaxonomyEnrichmentOutcome.InvalidResponse };
        return new(result);
    }

    private static TaxonomyEnrichmentResult DetailResult(CatalogMetadataSection section, IgdbMetadataStatus status,
        IgdbQueryFailure? failure, bool empty) => new(section, status switch
        {
            IgdbMetadataStatus.GameNotFound => TaxonomyEnrichmentOutcome.GameNotFound,
            IgdbMetadataStatus.TransportFailure => TaxonomyEnrichmentOutcome.ExternalFailure,
            IgdbMetadataStatus.Empty when empty && failure is null => TaxonomyEnrichmentOutcome.EmptyPreserved,
            _ => TaxonomyEnrichmentOutcome.InvalidResponse
        }, status, failure);

    private static bool ValidPlatformName(string? name) => !string.IsNullOrWhiteSpace(name) &&
        name.Length <= 200 && !name.Any(char.IsControl);

    private static async Task<bool> PersistTime(NextGameAiDbContext db, int gameId, IgdbTimeToBeatResult row, CancellationToken ct)
    {
        if (await db.GameTimeToBeats.AnyAsync(t => t.IgdbId == row.IgdbId && t.GameId != gameId, ct)) return false;
        var target = await db.GameTimeToBeats.SingleOrDefaultAsync(t => t.GameId == gameId, ct);
        if (target is null)
        {
            target = new GameTimeToBeat { GameId = gameId };
            db.GameTimeToBeats.Add(target);
        }
        target.IgdbId = row.IgdbId;
        target.HastilySeconds = row.HastilySeconds;
        target.NormallySeconds = row.NormallySeconds;
        target.CompletelySeconds = row.CompletelySeconds;
        target.SubmissionCount = row.SubmissionCount;
        target.Source = row.Source;
        return true;
    }

    private static async Task<bool> PersistMultiplayer(NextGameAiDbContext db, int gameId,
        IgdbMultiplayerResult[] rows, CancellationToken ct)
    {
        var current = await db.GameMultiplayerModes.Include(m => m.CatalogPlatform).Where(m => m.GameId == gameId).ToListAsync(ct);
        var ids = rows.Select(r => r.IgdbId).ToArray();
        if (await db.GameMultiplayerModes.AnyAsync(m => m.GameId != gameId && m.IgdbId.HasValue && ids.Contains(m.IgdbId.Value), ct)) return false;

        // Check all identities before tracking any platform/name/row mutation. A conflict rejects the section.
        foreach (var row in rows)
        {
            var byPlatform = current.SingleOrDefault(m => m.CatalogPlatform?.IgdbId == row.Platform?.IgdbId);
            var byRecord = current.SingleOrDefault(m => m.IgdbId == row.IgdbId);
            if (byRecord is not null && !ReferenceEquals(byRecord, byPlatform) ||
                byPlatform?.IgdbId is long oldId && oldId != row.IgdbId) return false;
        }

        foreach (var row in rows)
        {
            CatalogPlatform? platform = null;
            if (row.Platform is not null)
            {
                var externalPlatform = row.Platform;
                platform = await db.CatalogPlatforms.SingleOrDefaultAsync(p => p.IgdbId == externalPlatform.IgdbId, ct);
                if (platform is null)
                {
                    platform = new CatalogPlatform { IgdbId = externalPlatform.IgdbId, Name = externalPlatform.Name.Trim() };
                    db.CatalogPlatforms.Add(platform);
                }
                else if (platform.Name != externalPlatform.Name.Trim()) platform.Name = externalPlatform.Name.Trim();
            }
            var target = current.SingleOrDefault(m => m.CatalogPlatform?.IgdbId == row.Platform?.IgdbId);
            if (target is null)
            {
                target = new GameMultiplayerMode { GameId = gameId, CatalogPlatform = platform };
                db.GameMultiplayerModes.Add(target);
            }
            target.IgdbId = row.IgdbId;
            target.CampaignCoop = row.CampaignCoop;
            target.DropIn = row.DropIn;
            target.LanCoop = row.LanCoop;
            target.OfflineCoop = row.OfflineCoop;
            target.OnlineCoop = row.OnlineCoop;
            target.SplitScreen = row.SplitScreen;
            target.SplitScreenOnline = row.SplitScreenOnline;
            target.OfflineCoopMax = row.OfflineCoopMax;
            target.OfflineMax = row.OfflineMax;
            target.OnlineCoopMax = row.OnlineCoopMax;
            target.OnlineMax = row.OnlineMax;
            target.Source = row.Source;
        }
        // No snapshot completeness signal exists in 4B.2: never delete unreturned rows or shared platforms.
        return true;
    }
}
