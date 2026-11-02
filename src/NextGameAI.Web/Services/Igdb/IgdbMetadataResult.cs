using System.Net;

namespace NextGameAI.Web.Services.Igdb;

public enum IgdbMetadataStatus { Success, Empty, GameNotFound, InvalidResponse, TransportFailure, NotRequested }
public enum IgdbMetadataSectionKind { Genres, Themes, GameModes, PlayerPerspectives, Keywords, TimeToBeat, Multiplayer }
public enum IgdbTaxonomyKind { Genre, Theme, GameMode, PlayerPerspective, Keyword }

// Results are backend contracts, never added to the public MVC search projection.
// Printers deliberately omit payloads, including potentially revealing keyword names.
public sealed record IgdbMetadataSection<T>(IgdbMetadataStatus Status, T? Value = null,
    IgdbQueryFailure? Failure = null, HttpStatusCode? StatusCode = null, TimeSpan? RetryAfter = null) where T : class
{
    public override string ToString() => $"{nameof(IgdbMetadataSection<T>)}: {Status}";
}

public sealed record IgdbTaxonomyResult(IgdbTaxonomyKind Kind, long IgdbId, string Name)
{
    public override string ToString() => nameof(IgdbTaxonomyResult);
}

public sealed record IgdbTimeToBeatResult(long IgdbId, long GameIgdbId, long? HastilySeconds,
    long? NormallySeconds, long? CompletelySeconds, int? SubmissionCount)
{
    public string Source => "IGDB";
}

public sealed record IgdbPlatformResult(long IgdbId, string Name);

public sealed record IgdbMultiplayerResult(long IgdbId, long GameIgdbId, IgdbPlatformResult? Platform,
    bool? CampaignCoop, bool? DropIn, bool? LanCoop, bool? OfflineCoop, bool? OnlineCoop,
    bool? SplitScreen, bool? SplitScreenOnline, int? OfflineCoopMax, int? OfflineMax,
    int? OnlineCoopMax, int? OnlineMax)
{
    public string Source => "IGDB";
}

public sealed record IgdbMetadataResult(long IgdbId,
    IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Genres,
    IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Themes,
    IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> GameModes,
    IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> PlayerPerspectives,
    IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Keywords,
    IgdbMetadataSection<IgdbTimeToBeatResult> TimeToBeat,
    IgdbMetadataSection<IReadOnlyList<IgdbMultiplayerResult>> Multiplayer)
{
    public override string ToString() => nameof(IgdbMetadataResult);
}
