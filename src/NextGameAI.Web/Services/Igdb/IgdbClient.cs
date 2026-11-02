using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NextGameAI.Web.Services.Igdb;

public sealed class IgdbClient(IgdbApiTransport transport, IgdbQueryCache cache) : IIgdbClient
{
    private const string Fields = "fields id,name,cover.image_id,first_release_date,game_type.id,game_type.type,version_title,version_parent;";
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };

    public Task<IReadOnlyList<IgdbGameResult>> SearchAsync(string term, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(term);
        var literal = normalized.Replace("\\", "\\\\").Replace("\"", "\\\"");
        return QueryAsync($"search:{normalized.ToUpperInvariant()}", $"search \"{literal}\"; {Fields} limit 30;", null, cancellationToken, normalized);
    }

    public async Task<IgdbGameResult?> GetByIdAsync(long igdbId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (igdbId <= 0) throw new IgdbQueryException(IgdbQueryFailure.InvalidInput);
        var id = igdbId.ToString(CultureInfo.InvariantCulture);
        return (await QueryAsync($"id:{id}", $"{Fields} where id = {id}; limit 1;", igdbId, cancellationToken)).SingleOrDefault();
    }

    private static string Normalize(string? term)
    {
        if (string.IsNullOrWhiteSpace(term) || term.Length > 1024 || term.Any(c => char.IsControl(c) ||
            char.GetUnicodeCategory(c) is UnicodeCategory.Format or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator))
            throw new IgdbQueryException(IgdbQueryFailure.InvalidInput);
        string normalized;
        try { normalized = Regex.Replace(term.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " "); }
        catch (ArgumentException) { throw new IgdbQueryException(IgdbQueryFailure.InvalidInput); }
        if (normalized.Length is < 3 or > 100) throw new IgdbQueryException(IgdbQueryFailure.InvalidInput);
        return normalized;
    }

    private async Task<IReadOnlyList<IgdbGameResult>> QueryAsync(string key, string query, long? expectedId, CancellationToken cancellationToken, string? searchTerm = null)
    {
        transport.CheckConfiguration();
        if (cache.TryGet(key, out var cached)) return cached;
        var games = await transport.SendAsync<IReadOnlyList<IgdbGameResult>>("games", query, bytes =>
        {
            var payload = JsonSerializer.Deserialize<List<IgdbGameResponse?>>(bytes, JsonOptions);
            if (payload is null || payload.Count > 100) throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
            IReadOnlyList<IgdbGameResult> results = Array.AsReadOnly(payload.Select(Map).DistinctBy(x => x.IgdbId).ToArray());
            if (expectedId.HasValue && (results.Count > 1 || results.Any(x => x.IgdbId != expectedId.Value)))
                throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
            if (searchTerm is not null) results = IgdbSearchRelevance.Rank(results.Take(30), searchTerm);
            return results;
        }, cancellationToken, () => cache.TryGet(key, out var completed) ? completed : null);
        cache.Set(key, games);
        return games;
    }

    private static IgdbGameResult Map(IgdbGameResponse? game)
    {
        if (game is null || game.Id <= 0 || string.IsNullOrWhiteSpace(game.Name) || game.Name.Length > 300 ||
            game.Name.Any(char.IsControl) || game.VersionTitle?.Length > 200 || game.VersionParent is <= 0 ||
            game.GameType?.Id is < 0 || game.GameType?.Type?.Length > 100 ||
            game.VersionTitle?.Any(char.IsControl) == true || game.GameType?.Type?.Any(char.IsControl) == true ||
            (!string.IsNullOrWhiteSpace(game.Cover?.ImageId) && !IgdbCoverImages.IsValidId(game.Cover.ImageId)))
            throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
        DateOnly? date = game.FirstReleaseDate.HasValue
            ? DateOnly.FromDateTime(DateTimeOffset.FromUnixTimeSeconds(game.FirstReleaseDate.Value).UtcDateTime) : null;
        return new IgdbGameResult(game.Id, game.Name.Trim(), Clean(game.Cover?.ImageId), date,
            game.GameType?.Id, Clean(game.GameType?.Type), Clean(game.VersionTitle), game.VersionParent);
    }

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}
