using System.Globalization;
using System.Text.Json;

namespace NextGameAI.Web.Services.Igdb;

public sealed class IgdbMetadataClient(IgdbApiTransport transport) : IIgdbMetadataClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private const string TaxonomyFields = "id,genres.id,genres.name,themes.id,themes.name,game_modes.id,game_modes.name,player_perspectives.id,player_perspectives.name,keywords.id,keywords.name";
    private const string MultiplayerFields = "id,game,platform.id,platform.name,campaigncoop,dropin,lancoop,offlinecoop,onlinecoop,splitscreen,splitscreenonline,offlinecoopmax,offlinemax,onlinecoopmax,onlinemax";

    public async Task<IgdbMetadataResult> GetByIdAsync(long igdbId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (igdbId <= 0) throw new IgdbQueryException(IgdbQueryFailure.InvalidInput);
        var id = igdbId.ToString(CultureInfo.InvariantCulture);
        IgdbMetadataResponse? game;
        try
        {
            var response = await transport.SendAsync("games", $"fields {TaxonomyFields}; where id = {id}; limit 2;", bytes =>
            {
                var rows = Read<IgdbMetadataResponse>(bytes);
                if (rows.Length > 1 || rows.Any(g => g.Id != igdbId)) throw Invalid();
                return new GameResponse(rows.SingleOrDefault());
            }, cancellationToken);
            game = response.Game;
        }
        catch (IgdbQueryException error) { return Failed(igdbId, error); }
        if (game is null) return Failed(igdbId, null);

        var genres = Taxonomy(game.Genres, IgdbTaxonomyKind.Genre);
        var themes = Taxonomy(game.Themes, IgdbTaxonomyKind.Theme);
        var modes = Taxonomy(game.GameModes, IgdbTaxonomyKind.GameMode);
        var perspectives = Taxonomy(game.PlayerPerspectives, IgdbTaxonomyKind.PlayerPerspective);
        var keywords = Taxonomy(game.Keywords, IgdbTaxonomyKind.Keyword);
        var time = await SectionAsync("game_time_to_beats",
            $"fields id,game_id,hastily,normally,completely,count; where game_id = {id}; limit 2;",
            bytes => Time(Read<IgdbTimeToBeatResponse>(bytes), igdbId), cancellationToken);
        var multiplayer = await SectionAsync("multiplayer_modes",
            $"fields {MultiplayerFields}; where game = {id}; limit 100;",
            bytes => Multiplayer(Read<IgdbMultiplayerResponse>(bytes), igdbId), cancellationToken);
        return new(igdbId, genres, themes, modes, perspectives, keywords, time, multiplayer);
    }

    private async Task<IgdbMetadataSection<T>> SectionAsync<T>(string endpoint, string query,
        Func<byte[], IgdbMetadataSection<T>> parse, CancellationToken ct) where T : class
    {
        try { return await transport.SendAsync(endpoint, query, parse, ct); }
        catch (IgdbQueryException error) { return Failure<T>(error); }
    }

    private static IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Taxonomy(JsonElement? section, IgdbTaxonomyKind kind)
    {
        try
        {
            if (Absent(section)) return new(IgdbMetadataStatus.Empty, Array.AsReadOnly(Array.Empty<IgdbTaxonomyResult>()));
            if (section!.Value.ValueKind != JsonValueKind.Array) throw Invalid();
            var rows = section.Value.Deserialize<IgdbNamedReferenceResponse?[]>(JsonOptions) ?? throw Invalid();
            var ids = new HashSet<long>();
            var values = new List<IgdbTaxonomyResult>();
            foreach (var row in rows)
            {
                if (row?.Id is not > 0 || !ValidName(row.Name, 500) || !ids.Add(row.Id.Value)) throw Invalid();
                values.Add(new(kind, row.Id.Value, row.Name!.Trim()));
            }
            return new(values.Count == 0 ? IgdbMetadataStatus.Empty : IgdbMetadataStatus.Success, values.AsReadOnly());
        }
        catch (JsonException) { return Failure<IReadOnlyList<IgdbTaxonomyResult>>(Invalid()); }
        catch (IgdbQueryException error) { return Failure<IReadOnlyList<IgdbTaxonomyResult>>(error); }
    }

    private static IgdbMetadataSection<IgdbTimeToBeatResult> Time(IgdbTimeToBeatResponse[] rows, long gameId)
    {
        if (rows.Length == 0) return new(IgdbMetadataStatus.Empty);
        if (rows.Length != 1) throw Invalid();
        var row = rows[0];
        if (row.Id is not > 0 || row.GameId != gameId || row.Hastily is < 0 || row.Normally is < 0 ||
            row.Completely is < 0 || row.Count is < 0) throw Invalid();
        return new(IgdbMetadataStatus.Success, new(row.Id.Value, gameId, row.Hastily, row.Normally, row.Completely, row.Count));
    }

    private static IgdbMetadataSection<IReadOnlyList<IgdbMultiplayerResult>> Multiplayer(IgdbMultiplayerResponse[] rows, long gameId)
    {
        // A full page might be truncated. Never label that collection complete or raise the transport limits.
        if (rows.Length >= 100) throw Invalid();
        var ids = new HashSet<long>();
        var platforms = new HashSet<long?>();
        var values = new List<IgdbMultiplayerResult>();
        foreach (var row in rows)
        {
            if (row.Id is not > 0 || row.Game != gameId || !ids.Add(row.Id.Value) ||
                row.OfflineCoopMax is < 0 || row.OfflineMax is < 0 || row.OnlineCoopMax is < 0 || row.OnlineMax is < 0) throw Invalid();
            IgdbPlatformResult? platform = null;
            if (!Absent(row.Platform))
            {
                if (row.Platform!.Value.ValueKind != JsonValueKind.Object) throw Invalid();
                var reference = row.Platform.Value.Deserialize<IgdbNamedReferenceResponse>(JsonOptions);
                if (reference?.Id is not > 0 || !ValidName(reference.Name, 200)) throw Invalid();
                platform = new(reference.Id.Value, reference.Name!.Trim());
            }
            // The 4A schema admits only one row per exact external platform, including unknown.
            if (!platforms.Add(platform?.IgdbId)) throw Invalid();
            values.Add(new(row.Id.Value, gameId, platform, row.CampaignCoop, row.DropIn, row.LanCoop,
                row.OfflineCoop, row.OnlineCoop, row.SplitScreen, row.SplitScreenOnline, row.OfflineCoopMax,
                row.OfflineMax, row.OnlineCoopMax, row.OnlineMax));
        }
        return new(values.Count == 0 ? IgdbMetadataStatus.Empty : IgdbMetadataStatus.Success, values.AsReadOnly());
    }

    private static T[] Read<T>(byte[] bytes) where T : class
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        RejectDuplicateProperties(document.RootElement);
        var rows = document.RootElement.Deserialize<T?[]>(JsonOptions);
        if (rows is null || rows.Any(x => x is null)) throw Invalid();
        return rows.Select(x => x!).ToArray();
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw Invalid();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static bool Absent(JsonElement? value) => !value.HasValue || value.Value.ValueKind == JsonValueKind.Null;
    private static bool ValidName(string? name, int limit) => !string.IsNullOrWhiteSpace(name) && name.Length <= limit && !name.Any(char.IsControl);
    private static IgdbQueryException Invalid() => new(IgdbQueryFailure.InvalidResponse);

    private static IgdbMetadataSection<T> Failure<T>(IgdbQueryException? error) where T : class =>
        new(error is null ? IgdbMetadataStatus.GameNotFound : error.Failure == IgdbQueryFailure.InvalidResponse
            ? IgdbMetadataStatus.InvalidResponse : IgdbMetadataStatus.TransportFailure,
            Failure: error?.Failure, StatusCode: error?.StatusCode, RetryAfter: error?.RetryAfter);

    private static IgdbMetadataResult Failed(long id, IgdbQueryException? error) => new(id,
        Failure<IReadOnlyList<IgdbTaxonomyResult>>(error), Failure<IReadOnlyList<IgdbTaxonomyResult>>(error),
        Failure<IReadOnlyList<IgdbTaxonomyResult>>(error), Failure<IReadOnlyList<IgdbTaxonomyResult>>(error),
        Failure<IReadOnlyList<IgdbTaxonomyResult>>(error), Failure<IgdbTimeToBeatResult>(error),
        Failure<IReadOnlyList<IgdbMultiplayerResult>>(error));

    private sealed record GameResponse(IgdbMetadataResponse? Game);
}
