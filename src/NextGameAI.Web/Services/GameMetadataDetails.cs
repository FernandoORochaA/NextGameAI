using NextGameAI.Web.Models;

namespace NextGameAI.Web.Services;

// Presentation-safe catalog snapshots. No EF entities, keyword collections or execution decisions.
public sealed record GameMetadataDetails(int GameId,
    IReadOnlyList<GameMetadataTaxonomy> Genres,
    IReadOnlyList<GameMetadataTaxonomy> Themes,
    IReadOnlyList<GameMetadataTaxonomy> GameModes,
    IReadOnlyList<GameMetadataTaxonomy> PlayerPerspectives,
    GameMetadataDuration? TimeToBeat,
    IReadOnlyList<GameMetadataMultiplayer> MultiplayerModes,
    IReadOnlyList<GameMetadataSectionState> States);

public sealed record GameMetadataTaxonomy(long IgdbId, string Name);
public sealed record GameMetadataDuration(long? IgdbId, long? HastilySeconds, long? NormallySeconds,
    long? CompletelySeconds, int? SubmissionCount, string Source);
public sealed record GameMetadataPlatform(long IgdbId, string Name);
public sealed record GameMetadataMultiplayer(long? IgdbId, GameMetadataPlatform? Platform,
    bool? CampaignCoop, bool? DropIn, bool? LanCoop, bool? OfflineCoop, bool? OnlineCoop,
    bool? SplitScreen, bool? SplitScreenOnline, int? OfflineCoopMax, int? OfflineMax,
    int? OnlineCoopMax, int? OnlineMax, string Source);

// Missing timestamps/result mean no persisted state, not proof that content never existed.
// TemporaryFailure intentionally does not reconstruct the original detailed failure reason.
public sealed record GameMetadataSectionState(CatalogMetadataSection Section,
    CatalogUpdateResult? LastResult, DateTime? LastAttemptAtUtc, DateTime? LastSuccessAtUtc,
    string? Source, EnrichmentFreshness Freshness, DateTime? ExpiresAtUtc);
