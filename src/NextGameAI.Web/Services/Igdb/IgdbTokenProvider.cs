using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;

namespace NextGameAI.Web.Services.Igdb;

public sealed class IgdbTokenProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<IgdbOptions> options,
    TimeProvider timeProvider) : IIgdbTokenProvider, IDisposable
{
    public const string HttpClientName = "TwitchAuthentication";
    private readonly IgdbOptions configuration = options.Value;
    private readonly SemaphoreSlim gate = new(1, 1);
    private TokenState? cached;

    public bool IsConfigured => configuration.Enabled &&
        !string.IsNullOrWhiteSpace(configuration.ClientId) &&
        !string.IsNullOrWhiteSpace(configuration.ClientSecret) &&
        configuration.TimeoutSeconds is >= 1 and <= 60;

    public async Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckConfiguration();
        var current = Volatile.Read(ref cached);
        if (IsValid(current)) return current!.Value;

        await gate.WaitAsync(cancellationToken);
        try
        {
            current = Volatile.Read(ref cached);
            if (IsValid(current)) return current!.Value;
            var token = await RequestTokenAsync(cancellationToken);
            Volatile.Write(ref cached, token);
            return token.Value;
        }
        finally { gate.Release(); }
    }

    public void Invalidate(string rejectedToken)
    {
        var current = Volatile.Read(ref cached);
        if (current is not null && string.Equals(current.Value, rejectedToken, StringComparison.Ordinal))
            Interlocked.CompareExchange(ref cached, null, current);
    }

    private bool IsValid(TokenState? token) => token is not null && timeProvider.GetUtcNow() < token.ReuseUntil;

    private void CheckConfiguration()
    {
        if (!configuration.Enabled) throw new IgdbAuthenticationException(IgdbAuthenticationFailure.Disabled);
        if (string.IsNullOrWhiteSpace(configuration.ClientId) || string.IsNullOrWhiteSpace(configuration.ClientSecret))
            throw new IgdbAuthenticationException(IgdbAuthenticationFailure.NotConfigured);
        if (configuration.TimeoutSeconds is < 1 or > 60)
            throw new IgdbAuthenticationException(IgdbAuthenticationFailure.InvalidConfiguration);
    }

    private async Task<TokenState> RequestTokenAsync(CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(configuration.TimeoutSeconds));
        var startedAt = timeProvider.GetUtcNow();
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://id.twitch.tv/oauth2/token")
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = configuration.ClientId!,
                    ["client_secret"] = configuration.ClientSecret!,
                    ["grant_type"] = "client_credentials"
                })
            };
            using var response = await client.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode)
                throw new IgdbAuthenticationException(response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized
                    ? IgdbAuthenticationFailure.CredentialsRejected : IgdbAuthenticationFailure.HttpError, response.StatusCode);

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            var payload = await JsonSerializer.DeserializeAsync<TokenResponse>(stream, cancellationToken: timeout.Token);
            if (payload is null || string.IsNullOrWhiteSpace(payload.AccessToken) ||
                payload.AccessToken.Length > 8192 || !payload.AccessToken.All(c => char.IsAsciiLetterOrDigit(c) || "-._~+/=".Contains(c)) ||
                !string.Equals(payload.TokenType, "bearer", StringComparison.OrdinalIgnoreCase) || payload.ExpiresIn <= 0)
                throw new IgdbAuthenticationException(IgdbAuthenticationFailure.InvalidResponse);

            // Start at the request's beginning: transport time cannot extend the token's lifetime.
            var expiresAt = startedAt.AddSeconds(payload.ExpiresIn);
            var margin = TimeSpan.FromSeconds(Math.Min(60, payload.ExpiresIn * 0.1));
            var reuseUntil = expiresAt - margin;
            if (reuseUntil <= timeProvider.GetUtcNow())
                throw new IgdbAuthenticationException(IgdbAuthenticationFailure.InvalidResponse);
            return new TokenState(payload.AccessToken, reuseUntil);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("A autenticação foi cancelada.", cancellationToken);
        }
        catch (OperationCanceledException) { throw new IgdbAuthenticationException(IgdbAuthenticationFailure.Timeout); }
        catch (HttpRequestException) { throw new IgdbAuthenticationException(IgdbAuthenticationFailure.Unavailable); }
        catch (IOException) { throw new IgdbAuthenticationException(IgdbAuthenticationFailure.Unavailable); }
        catch (JsonException) { throw new IgdbAuthenticationException(IgdbAuthenticationFailure.InvalidResponse); }
        catch (ArgumentOutOfRangeException) { throw new IgdbAuthenticationException(IgdbAuthenticationFailure.InvalidResponse); }
    }

    public void Dispose() => gate.Dispose();

    // Classes deliberately keep the default ToString(): no generated record printer can disclose a token.
    private sealed class TokenState(string value, DateTimeOffset reuseUntil)
    {
        public string Value { get; } = value;
        public DateTimeOffset ReuseUntil { get; } = reuseUntil;
    }

    private sealed class TokenResponse
    {
        [JsonPropertyName("access_token")] public string? AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public long ExpiresIn { get; set; }
        [JsonPropertyName("token_type")] public string? TokenType { get; set; }
    }
}
