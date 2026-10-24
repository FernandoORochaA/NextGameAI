using System.ComponentModel.DataAnnotations;

namespace NextGameAI.Web.Models;

public enum LibraryStatus
{
    [Display(Name = "Não iniciado")] NotStarted = 0,
    [Display(Name = "Jogando")] Playing = 1,
    [Display(Name = "Finalizado")] Completed = 2,
    [Display(Name = "Abandonado")] Abandoned = 3
}

public class UserProfile
{
    public int Id { get; set; }
    [Required, StringLength(100)] public string DisplayName { get; set; } = "";
    public List<LibraryEntry> LibraryEntries { get; set; } = [];
}

public class Game
{
    public int Id { get; set; }
    [Range(1, long.MaxValue)] public long? IgdbId { get; set; }
    [Required, StringLength(300)] public string Title { get; set; } = "";
    [StringLength(200)] public string? EditionTitle { get; set; }
    [StringLength(128)] public string? CoverImageId { get; set; }
    public DateOnly? FirstReleaseDate { get; set; }
    [Range(0, long.MaxValue)] public long? GameTypeId { get; set; }
    [StringLength(100)] public string? GameType { get; set; }
    public List<LibraryEntry> LibraryEntries { get; set; } = [];
}

public class LibraryEntry
{
    public int Id { get; set; }
    public int UserProfileId { get; set; }
    public UserProfile UserProfile { get; set; } = null!;
    public int GameId { get; set; }
    public Game Game { get; set; } = null!;
    [EnumDataType(typeof(LibraryStatus))] public LibraryStatus Status { get; set; }
    // Units of half a star: null = unrated, 0 = zero stars, 10 = five stars.
    [Range(0, 10)] public int? Rating { get; set; }
    [StringLength(5000)] public string? Notes { get; set; }
    [StringLength(1000)] public string? AbandonmentReason { get; set; }
    public DateTime AddedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
    public List<GameOwnership> Ownerships { get; set; } = [];
}

public class Store
{
    public int Id { get; set; }
    [Required, StringLength(50)] public string Code { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
}

public class Platform
{
    public int Id { get; set; }
    [Required, StringLength(50)] public string Code { get; set; } = "";
    [Required, StringLength(100)] public string Name { get; set; } = "";
    [Range(1, long.MaxValue)] public long? IgdbId { get; set; }
}

public class GameOwnership
{
    public int Id { get; set; }
    public int LibraryEntryId { get; set; }
    public LibraryEntry LibraryEntry { get; set; } = null!;
    public int StoreId { get; set; }
    public Store Store { get; set; } = null!;
    public int PlatformId { get; set; }
    public Platform Platform { get; set; } = null!;
}
