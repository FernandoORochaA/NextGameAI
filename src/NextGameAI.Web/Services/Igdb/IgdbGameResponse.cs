using System.Text.Json.Serialization;

namespace NextGameAI.Web.Services.Igdb;

internal sealed class IgdbGameResponse
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("cover")] public CoverResponse? Cover { get; set; }
    [JsonPropertyName("first_release_date")] public long? FirstReleaseDate { get; set; }
    [JsonPropertyName("game_type")] public GameTypeResponse? GameType { get; set; }
    [JsonPropertyName("version_title")] public string? VersionTitle { get; set; }
    [JsonPropertyName("version_parent")] public long? VersionParent { get; set; }

    internal sealed class CoverResponse
    { [JsonPropertyName("image_id")] public string? ImageId { get; set; } }

    internal sealed class GameTypeResponse
    {
        [JsonPropertyName("id")] public long? Id { get; set; }
        [JsonPropertyName("type")] public string? Type { get; set; }
    }
}
