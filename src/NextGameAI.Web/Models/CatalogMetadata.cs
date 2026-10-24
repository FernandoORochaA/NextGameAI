using System.ComponentModel.DataAnnotations;

namespace NextGameAI.Web.Models;

public enum CatalogTaxonomyKind { Genre, Theme, GameMode, PlayerPerspective, Keyword }

public class CatalogTaxonomy
{
    public int Id { get; set; }
    [EnumDataType(typeof(CatalogTaxonomyKind))] public CatalogTaxonomyKind Kind { get; set; }
    [Range(1, long.MaxValue)] public long IgdbId { get; set; }
    [Required, StringLength(500)] public string Name { get; set; } = "";
}

public class GameTaxonomy
{
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    public int CatalogTaxonomyId { get; set; }
    public CatalogTaxonomy CatalogTaxonomy { get; set; } = null!;
}

public class GameTimeToBeat
{
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    [Range(1, long.MaxValue)] public long? IgdbId { get; set; }
    [Range(0, long.MaxValue)] public long? HastilySeconds { get; set; }
    [Range(0, long.MaxValue)] public long? NormallySeconds { get; set; }
    [Range(0, long.MaxValue)] public long? CompletelySeconds { get; set; }
    [Range(0, int.MaxValue)] public int? SubmissionCount { get; set; }
    [Required, StringLength(50)] public string Source { get; set; } = "IGDB";
}

// External release platform; never a personal ownership or a simplified platform group.
public class CatalogPlatform
{
    public int Id { get; set; }
    [Range(1, long.MaxValue)] public long IgdbId { get; set; }
    [Required, StringLength(200)] public string Name { get; set; } = "";
}

public class GameMultiplayerMode
{
    public int Id { get; set; }
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    [Range(1, long.MaxValue)] public long? IgdbId { get; set; }
    public int? CatalogPlatformId { get; set; }
    public CatalogPlatform? CatalogPlatform { get; set; }
    public bool? CampaignCoop { get; set; }
    public bool? DropIn { get; set; }
    public bool? LanCoop { get; set; }
    public bool? OfflineCoop { get; set; }
    public bool? OnlineCoop { get; set; }
    public bool? SplitScreen { get; set; }
    public bool? SplitScreenOnline { get; set; }
    [Range(0, int.MaxValue)] public int? OfflineCoopMax { get; set; }
    [Range(0, int.MaxValue)] public int? OfflineMax { get; set; }
    [Range(0, int.MaxValue)] public int? OnlineCoopMax { get; set; }
    [Range(0, int.MaxValue)] public int? OnlineMax { get; set; }
    [Required, StringLength(50)] public string Source { get; set; } = "IGDB";
}

public enum CatalogMetadataSection { Genres, Themes, GameModes, PlayerPerspectives, Keywords, TimeToBeat, Multiplayer }
public enum CatalogUpdateResult { Success, TemporaryFailure, NotFound }

// No row = never queried. Freshness is derived later from LastSuccessAtUtc, never stored as a flag.
public class GameEnrichmentState
{
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    [EnumDataType(typeof(CatalogMetadataSection))] public CatalogMetadataSection Section { get; set; }
    public DateTime LastAttemptAtUtc { get; set; }
    public DateTime? LastSuccessAtUtc { get; set; }
    [EnumDataType(typeof(CatalogUpdateResult))] public CatalogUpdateResult LastResult { get; set; }
    [Required, StringLength(50)] public string Source { get; set; } = "IGDB";
}
