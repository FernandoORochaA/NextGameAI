using System.Text.Json;
using System.Text.Json.Serialization;

namespace NextGameAI.Web.Services.Igdb;

// Untyped section boundaries isolate a malformed taxonomy from otherwise valid siblings.
internal sealed class IgdbMetadataResponse
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("genres")] public JsonElement? Genres { get; set; }
    [JsonPropertyName("themes")] public JsonElement? Themes { get; set; }
    [JsonPropertyName("game_modes")] public JsonElement? GameModes { get; set; }
    [JsonPropertyName("player_perspectives")] public JsonElement? PlayerPerspectives { get; set; }
    [JsonPropertyName("keywords")] public JsonElement? Keywords { get; set; }
}

internal sealed class IgdbNamedReferenceResponse
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
}

internal sealed class IgdbTimeToBeatResponse
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("game_id")] public long? GameId { get; set; }
    [JsonPropertyName("hastily")] public long? Hastily { get; set; }
    [JsonPropertyName("normally")] public long? Normally { get; set; }
    [JsonPropertyName("completely")] public long? Completely { get; set; }
    [JsonPropertyName("count")] public int? Count { get; set; }
}

internal sealed class IgdbMultiplayerResponse
{
    [JsonPropertyName("id")] public long? Id { get; set; }
    [JsonPropertyName("game")] public long? Game { get; set; }
    [JsonPropertyName("platform")] public JsonElement? Platform { get; set; }
    [JsonPropertyName("campaigncoop")] public bool? CampaignCoop { get; set; }
    [JsonPropertyName("dropin")] public bool? DropIn { get; set; }
    [JsonPropertyName("lancoop")] public bool? LanCoop { get; set; }
    [JsonPropertyName("offlinecoop")] public bool? OfflineCoop { get; set; }
    [JsonPropertyName("onlinecoop")] public bool? OnlineCoop { get; set; }
    [JsonPropertyName("splitscreen")] public bool? SplitScreen { get; set; }
    [JsonPropertyName("splitscreenonline")] public bool? SplitScreenOnline { get; set; }
    [JsonPropertyName("offlinecoopmax")] public int? OfflineCoopMax { get; set; }
    [JsonPropertyName("offlinemax")] public int? OfflineMax { get; set; }
    [JsonPropertyName("onlinecoopmax")] public int? OnlineCoopMax { get; set; }
    [JsonPropertyName("onlinemax")] public int? OnlineMax { get; set; }
}
