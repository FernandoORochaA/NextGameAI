using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;

namespace NextGameAI.Web.Services.Igdb;

// IGDB-only transport. Parsing and cache ownership stay with each consuming client.
public sealed class IgdbApiTransport(HttpClient http, IIgdbTokenProvider tokens,
    IOptions<IgdbOptions> options, IgdbRequestScheduler scheduler, TimeProvider timeProvider)
{
    private const int MaximumResponseBytes = 256 * 1024;
    private readonly IgdbOptions configuration = options.Value;

    internal void CheckConfiguration()
    {
        if (!configuration.Enabled) throw new IgdbQueryException(IgdbQueryFailure.Disabled);
        if (configuration.TimeoutSeconds is < 1 or > 60)
            throw new IgdbQueryException(IgdbQueryFailure.InvalidConfiguration);
        if (string.IsNullOrWhiteSpace(configuration.ClientId) || !tokens.IsConfigured)
            throw new IgdbQueryException(IgdbQueryFailure.NotConfigured);
        if (configuration.ClientId.Any(c => !char.IsAsciiLetterOrDigit(c)))
            throw new IgdbQueryException(IgdbQueryFailure.InvalidConfiguration);
    }

    public async Task<T> SendAsync<T>(string endpoint, string query, Func<byte[], T> parse,
        CancellationToken cancellationToken, Func<T?>? readCached = null) where T : class
    {
        // Only a resource name is accepted; callers cannot redirect authenticated traffic.
        if (string.IsNullOrEmpty(endpoint) || endpoint.Any(c => !(c is >= 'a' and <= 'z' or '_')))
            throw new IgdbQueryException(IgdbQueryFailure.InvalidInput);
        CheckConfiguration();
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
                    var completed = readCached?.Invoke();
                    if (completed is not null) return new Outcome<T>(HttpStatusCode.OK, completed, null);
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"https://api.igdb.com/v4/{endpoint}")
                    { Content = new StringContent(query, Encoding.UTF8, "text/plain") };
                    request.Headers.Add("Client-ID", configuration.ClientId);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                    using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (response.StatusCode == HttpStatusCode.TooManyRequests)
                    {
                        var wait = GetRetryAfter(response);
                        scheduler.Defer(wait);
                        return new Outcome<T>(response.StatusCode, null, wait);
                    }
                    if (!response.IsSuccessStatusCode) return new Outcome<T>(response.StatusCode, null, null);
                    var result = parse(await ReadResponseAsync(response, ct));
                    return new Outcome<T>(response.StatusCode, result, null);
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
                if (outcome.Result is null) throw new IgdbQueryException(IgdbQueryFailure.HttpError, outcome.Status);
                return outcome.Result;
            }
        }
        catch (IgdbAuthenticationException error) { throw new IgdbQueryException(IgdbQueryFailure.AuthenticationUnavailable, error.StatusCode); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("A consulta Ã  IGDB foi cancelada.", cancellationToken); }
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

    private static async Task<byte[]> ReadResponseAsync(HttpResponseMessage response, CancellationToken ct)
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
        return buffer.ToArray();
    }

    private sealed record Outcome<T>(HttpStatusCode Status, T? Result, TimeSpan? Wait);
}
