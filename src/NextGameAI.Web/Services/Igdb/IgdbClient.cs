using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;

namespace NextGameAI.Web.Services.Igdb;

public sealed class IgdbClient(HttpClient http, IIgdbTokenProvider tokens, IOptions<IgdbOptions> options,
    IgdbQueryCache cache, IgdbRequestScheduler scheduler, TimeProvider timeProvider) : IIgdbClient
{
    private const string Fields = "fields id,name,cover.image_id,first_release_date,game_type.id,game_type.type,version_title,version_parent;";
    private const int MaximumResponseBytes = 256 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { MaxDepth = 16 };
    private readonly IgdbOptions configuration = options.Value;

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

    private void CheckConfiguration()
    {
        if (!configuration.Enabled) throw new IgdbQueryException(IgdbQueryFailure.Disabled);
        if (configuration.TimeoutSeconds is < 1 or > 60)
            throw new IgdbQueryException(IgdbQueryFailure.InvalidConfiguration);
        if (string.IsNullOrWhiteSpace(configuration.ClientId) || !tokens.IsConfigured)
            throw new IgdbQueryException(IgdbQueryFailure.NotConfigured);
        if (configuration.ClientId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new IgdbQueryException(IgdbQueryFailure.InvalidConfiguration);
    }

    private async Task<IReadOnlyList<IgdbGameResult>> QueryAsync(string key, string query, long? expectedId, CancellationToken cancellationToken, string? searchTerm = null)
    {
        CheckConfiguration();
        if (cache.TryGet(key, out var cached)) return cached;
        var refreshed = false;
        var rateRetried = false;
        try
        {
            while (true)
            {
                var token = await tokens.GetTokenAsync(cancellationToken);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
                var outcome = await scheduler.ExecuteAsync(async ct =>
                {
                    if (cache.TryGet(key, out var completed)) return new Outcome(HttpStatusCode.OK, completed, null);
                    using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.igdb.com/v4/games")
                    { Content = new StringContent(query, Encoding.UTF8, "text/plain") };
                    request.Headers.Add("Client-ID", configuration.ClientId);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var wait = GetRetryAfter(response);
                        scheduler.Defer(wait);
                        return new Outcome(response.StatusCode, null, wait);
                    }
                    if (!response.IsSuccessStatusCode) return new Outcome(response.StatusCode, null, null);
                    var games = await ReadResponseAsync(response, ct);
                    if (expectedId.HasValue && (games.Count > 1 || games.Any(x => x.IgdbId != expectedId.Value)))
                        throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
                    if (searchTerm is not null) games = IgdbSearchRelevance.Rank(games.Take(30), searchTerm);
                    return new Outcome(response.StatusCode, games, null);
                }, timeout.Token);

                if (outcome.Status == HttpStatusCode.Unauthorized)
                {
                    tokens.Invalidate(token);
                    if (refreshed) throw new IgdbQueryException(IgdbQueryFailure.Unauthorized, outcome.Status);
                    refreshed = true;
                    continue;
                }
                if (outcome.Status == HttpStatusCode.TooManyRequests)
                {
                    if (rateRetried || outcome.Wait > IgdbRequestScheduler.MaximumWait)
                        throw new IgdbQueryException(IgdbQueryFailure.RateLimited, outcome.Status, outcome.Wait);
                    rateRetried = true;
                    continue;
                }
                if (outcome.Games is null) throw new IgdbQueryException(IgdbQueryFailure.HttpError, outcome.Status);
                cache.Set(key, outcome.Games);
                return outcome.Games;
            }
        }
        catch (IgdbAuthenticationException error) { throw new IgdbQueryException(IgdbQueryFailure.AuthenticationUnavailable, error.StatusCode); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("A consulta à IGDB foi cancelada.", cancellationToken); }
        catch (OperationCanceledException) { throw new IgdbQueryException(IgdbQueryFailure.Timeout); }
        catch (HttpRequestException) { throw new IgdbQueryException(IgdbQueryFailure.Unavailable); }
        catch (IOException) { throw new IgdbQueryException(IgdbQueryFailure.Unavailable); }
        catch (JsonException) { throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse); }
        catch (FormatException) { throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse); }
        catch (ArgumentOutOfRangeException) { throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse); }
    }

    private TimeSpan GetRetryAfter(HttpResponseMessage response)
    {
        var retry = response.Headers.RetryAfter;
        var delay = retry?.Delta ?? (retry?.Date - timeProvider.GetUtcNow()) ?? TimeSpan.FromSeconds(1);
        return delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    private static async Task<IReadOnlyList<IgdbGameResult>> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > MaximumResponseBytes)
            throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > MaximumResponseBytes) throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
            buffer.Write(chunk, 0, read);
        }
        var payload = JsonSerializer.Deserialize<List<IgdbGameResponse?>>(buffer.ToArray(), JsonOptions);
        if (payload is null || payload.Count > 100) throw new IgdbQueryException(IgdbQueryFailure.InvalidResponse);
        var results = payload.Select(Map).DistinctBy(x => x.IgdbId).ToArray();
        return Array.AsReadOnly(results);
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
    private sealed record Outcome(HttpStatusCode Status, IReadOnlyList<IgdbGameResult>? Games, TimeSpan? Wait);
}
