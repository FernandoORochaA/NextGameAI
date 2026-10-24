using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

public sealed class IgdbTokenProviderTests
{
    // Synthetic values only. No IConfiguration from the application or User Secrets is loaded here.
    private const string TestClient = "synthetic-client";
    private const string TestSecret = "synthetic-secret";
    private const string TestToken = "synthetic-token";

    private static HttpResponseMessage Success(string token = TestToken, long expires = 3600) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new { access_token = token, expires_in = expires, token_type = "bearer" }), Encoding.UTF8, "application/json")
    };

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan time) => now += time;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return send(request, cancellationToken);
        }
    }

    private sealed class Factory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(IgdbTokenProvider.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    private sealed class Fixture : IDisposable
    {
        public Clock Time { get; } = new();
        public Handler Http { get; }
        public IgdbTokenProvider Provider { get; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null, Action<IgdbOptions>? configure = null)
        {
            var options = new IgdbOptions { ClientId = TestClient, ClientSecret = TestSecret };
            configure?.Invoke(options);
            Http = new(send ?? ((_, _) => Task.FromResult(Success())));
            Provider = new(new Factory(Http), Options.Create(options), Time);
        }
        public void Dispose() { Provider.Dispose(); Http.Dispose(); }
    }

    private static void AssertSafe(Exception error)
    {
        Assert.DoesNotContain(TestClient, error.ToString());
        Assert.DoesNotContain(TestSecret, error.ToString());
        Assert.DoesNotContain(TestToken, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task PostsFormToFixedEndpointAndReusesToken()
    {
        using var test = new Fixture(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://id.twitch.tv/oauth2/token", request.RequestUri!.AbsoluteUri);
            Assert.Empty(request.RequestUri.Query);
            Assert.Null(request.Headers.Authorization);
            Assert.Equal("application/x-www-form-urlencoded", request.Content!.Headers.ContentType!.MediaType);
            var form = await request.Content.ReadAsStringAsync();
            Assert.Contains($"client_id={TestClient}", form);
            Assert.Contains($"client_secret={TestSecret}", form);
            Assert.Contains("grant_type=client_credentials", form);
            return Success();
        });
        Assert.True(test.Provider.IsConfigured);
        Assert.Equal(TestToken, await test.Provider.GetTokenAsync());
        Assert.Equal(TestToken, await test.Provider.GetTokenAsync());
        Assert.Equal(1, test.Http.Calls);
    }

    [Fact]
    public async Task ReplacesExpiredTokenAndAppliesSafetyMargin()
    {
        var count = 0;
        using var test = new Fixture((_, _) => Task.FromResult(Success($"synthetic-token-{++count}", 100)));
        var first = await test.Provider.GetTokenAsync();
        test.Time.Advance(TimeSpan.FromSeconds(89));
        Assert.Equal(first, await test.Provider.GetTokenAsync());
        test.Time.Advance(TimeSpan.FromSeconds(1)); // 10% safety margin, before actual expiry.
        var second = await test.Provider.GetTokenAsync();
        Assert.NotEqual(first, second);
        test.Time.Advance(TimeSpan.FromSeconds(101));
        Assert.NotEqual(second, await test.Provider.GetTokenAsync());
        Assert.Equal(3, test.Http.Calls);
    }

    [Fact]
    public async Task ShortLivedTokenDoesNotUseFixedSixtySecondMargin()
    {
        using var test = new Fixture((_, _) => Task.FromResult(Success(expires: 1)));
        await test.Provider.GetTokenAsync();
        test.Time.Advance(TimeSpan.FromMilliseconds(800));
        await test.Provider.GetTokenAsync();
        Assert.Equal(1, test.Http.Calls);
        test.Time.Advance(TimeSpan.FromMilliseconds(100));
        await test.Provider.GetTokenAsync();
        Assert.Equal(2, test.Http.Calls);
    }

    [Fact]
    public async Task ConcurrentRequestsShareSingleAcquisitionAndRenewal()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var test = new Fixture(async (_, cancellation) =>
        {
            started.TrySetResult(); await release.Task.WaitAsync(cancellation); return Success(expires: 100);
        });
        var requests = Enumerable.Range(0, 20).Select(_ => test.Provider.GetTokenAsync()).ToArray();
        await started.Task;
        Assert.Equal(1, test.Http.Calls);
        release.SetResult();
        Assert.All(await Task.WhenAll(requests), token => Assert.Equal(TestToken, token));
        test.Time.Advance(TimeSpan.FromSeconds(101));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => test.Provider.GetTokenAsync()));
        Assert.Equal(2, test.Http.Calls);
    }

    [Fact]
    public async Task OldUnauthorizedResponseCannotInvalidateNewToken()
    {
        var count = 0;
        using var test = new Fixture((_, _) => Task.FromResult(Success($"synthetic-token-{++count}")));
        var old = await test.Provider.GetTokenAsync();
        test.Provider.Invalidate(old);
        var current = await test.Provider.GetTokenAsync();
        test.Provider.Invalidate(old);
        Assert.Equal(current, await test.Provider.GetTokenAsync());
        Assert.Equal(2, test.Http.Calls);
        test.Provider.Invalidate(current);
        Assert.NotEqual(current, await test.Provider.GetTokenAsync());
    }

    [Theory]
    [InlineData("client")] [InlineData("secret")] [InlineData("blank")]
    [InlineData("disabled")] [InlineData("timeout-low")] [InlineData("timeout-high")]
    public async Task MissingOrInvalidConfigurationNeverMakesNetworkCall(string setting)
    {
        using var test = new Fixture(configure: options =>
        {
            switch (setting)
            {
                case "client": options.ClientId = null; break;
                case "secret": options.ClientSecret = null; break;
                case "blank": options.ClientSecret = "  "; break;
                case "disabled": options.Enabled = false; break;
                case "timeout-low": options.TimeoutSeconds = 0; break;
                case "timeout-high": options.TimeoutSeconds = 61; break;
            }
        });
        Assert.False(test.Provider.IsConfigured);
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => test.Provider.GetTokenAsync());
        AssertSafe(error);
        Assert.Equal(0, test.Http.Calls);
    }

    [Theory]
    [InlineData(400)] [InlineData(401)] [InlineData(429)] [InlineData(500)] [InlineData(503)] [InlineData(302)]
    public async Task HttpFailuresNeverDiscloseResponseBody(int status)
    {
        using var test = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
        { Content = new StringContent($"{TestClient} {TestSecret} {TestToken}") }));
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => test.Provider.GetTokenAsync());
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.Equal(status is 400 or 401 ? IgdbAuthenticationFailure.CredentialsRejected : IgdbAuthenticationFailure.HttpError, error.Failure);
        AssertSafe(error);
    }

    [Theory]
    [InlineData("null")] [InlineData("{}")] [InlineData("not json")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":0,\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":-1,\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":1.5,\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":\"synthetic-secret\",\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":9223372036854775807,\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":3600}")]
    [InlineData("{\"access_token\":\"synthetic-token\",\"expires_in\":3600,\"token_type\":\"invalid\"}")]
    [InlineData("{\"access_token\":\"\",\"expires_in\":3600,\"token_type\":\"bearer\"}")]
    [InlineData("{\"access_token\":\"invalid token\",\"expires_in\":3600,\"token_type\":\"bearer\"}")]
    public async Task RejectsInvalidResponsesWithoutLeakingJson(string json)
    {
        using var test = new Fixture((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) }));
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => test.Provider.GetTokenAsync());
        Assert.Equal(IgdbAuthenticationFailure.InvalidResponse, error.Failure);
        AssertSafe(error);
    }

    [Fact]
    public async Task HandlesTimeoutAndCanRecoverOnNextRequest()
    {
        var count = 0;
        using var test = new Fixture(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref count) == 1) await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            return Success();
        }, options => options.TimeoutSeconds = 1);
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => test.Provider.GetTokenAsync());
        Assert.Equal(IgdbAuthenticationFailure.Timeout, error.Failure);
        AssertSafe(error);
        Assert.Equal(TestToken, await test.Provider.GetTokenAsync());
    }

    [Fact]
    public async Task CallerCancellationReleasesGateWithoutLeakingUnderlyingError()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var test = new Fixture(async (_, cancellation) =>
        {
            if (Interlocked.Increment(ref count) == 1)
            {
                started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, cancellation);
            }
            return Success();
        });
        using var cancellation = new CancellationTokenSource();
        var first = test.Provider.GetTokenAsync(cancellation.Token);
        await started.Task; cancellation.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => first);
        AssertSafe(error);
        Assert.Equal(TestToken, await test.Provider.GetTokenAsync());
    }

    [Fact]
    public async Task CanceledWaiterDoesNotCancelSharedAcquisition()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var test = new Fixture(async (_, cancellation) =>
        { started.SetResult(); await release.Task.WaitAsync(cancellation); return Success(); });
        var first = test.Provider.GetTokenAsync(); await started.Task;
        using var cancellation = new CancellationTokenSource();
        var waiter = test.Provider.GetTokenAsync(cancellation.Token); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        release.SetResult(); Assert.Equal(TestToken, await first);
        Assert.Equal(1, test.Http.Calls);
    }

    [Fact]
    public async Task NetworkErrorsDoNotRetainSensitiveInnerExceptions()
    {
        using var test = new Fixture((_, _) => throw new HttpRequestException($"{TestClient} {TestSecret} {TestToken}"));
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => test.Provider.GetTokenAsync());
        Assert.Equal(IgdbAuthenticationFailure.Unavailable, error.Failure);
        AssertSafe(error);
    }

    [Fact]
    public async Task RegistrationBindsOptionsAndUsesSingletonWithoutRequestingToken()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Igdb:ClientId"] = TestClient, ["Igdb:ClientSecret"] = TestSecret, ["Igdb:TimeoutSeconds"] = "5"
        }).Build();
        var services = new ServiceCollection().AddLogging().AddIgdbAuthentication(configuration);
        using var root = services.BuildServiceProvider(validateScopes: true);
        var provider = root.GetRequiredService<IIgdbTokenProvider>();
        using var scope = root.CreateScope();
        Assert.Same(provider, scope.ServiceProvider.GetRequiredService<IIgdbTokenProvider>());
        Assert.True(provider.IsConfigured);
        Assert.Equal(5, root.GetRequiredService<IOptions<IgdbOptions>>().Value.TimeoutSeconds);
        // No GetTokenAsync call: registration and resolution must be safe offline.
        var emptyServices = new ServiceCollection().AddLogging().AddIgdbAuthentication(new ConfigurationBuilder().Build());
        using var emptyRoot = emptyServices.BuildServiceProvider();
        Assert.False(emptyRoot.GetRequiredService<IIgdbTokenProvider>().IsConfigured);
        var error = await Assert.ThrowsAsync<IgdbAuthenticationException>(() => emptyRoot.GetRequiredService<IIgdbTokenProvider>().GetTokenAsync());
        AssertSafe(error);
    }

    [Fact]
    public async Task LocalLibraryStillWorksWhenProviderIsUnconfigured()
    {
        using var app = new LibraryApplication();
        using var client = app.CreateClient(new() { BaseAddress = new Uri("https://localhost") });
        using var scope = app.Services.CreateScope();
        Assert.False(scope.ServiceProvider.GetRequiredService<IIgdbTokenProvider>().IsConfigured);
        Assert.True((await client.GetAsync("/Library")).IsSuccessStatusCode);
        Assert.True((await client.GetAsync("/Library/Create")).IsSuccessStatusCode);
    }
}
