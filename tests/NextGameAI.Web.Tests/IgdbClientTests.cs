using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;
using NextGameAI.Web.Data;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace NextGameAI.Web.Tests;

public sealed class IgdbClientTests
{
    private const string FakeClientId = "syntheticclient";
    private const string FakeToken = "synthetic-token";
    private const string FakeSecret = "synthetic-secret";
    private const string SingleGame = "[{\"id\":123,\"name\":\"Jogo de teste\"}]";

    private static HttpResponseMessage Json(string json = SingleGame, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    // A deterministic timer advances virtual time instead of sleeping for rate-limit/retry/cache tests.
    private sealed class Clock : TimeProvider
    {
        private long elapsed;
        private static readonly DateTimeOffset Origin = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Origin.AddTicks(Interlocked.Read(ref elapsed));
        public override long GetTimestamp() => Interlocked.Read(ref elapsed);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan span) => Interlocked.Add(ref elapsed, span.Ticks);
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new OnceTimer();
            ThreadPool.QueueUserWorkItem(_ =>
            {
                if (dueTime != Timeout.InfiniteTimeSpan && !timer.Disposed)
                { Advance(dueTime); callback(state); }
            });
            return timer;
        }
        private sealed class OnceTimer : ITimer
        {
            private int disposed;
            public bool Disposed => Volatile.Read(ref disposed) == 1;
            public bool Change(TimeSpan dueTime, TimeSpan period) => false;
            public void Dispose() => Interlocked.Exchange(ref disposed, 1);
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }

    private sealed class TokenProvider : IIgdbTokenProvider
    {
        public bool IsConfigured { get; set; } = true;
        public int Gets;
        public List<string> Invalidated { get; } = [];
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Gets); return Task.FromResult(FakeToken + Invalidated.Count); }
        public void Invalidate(string rejectedToken) => Invalidated.Add(rejectedToken);
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return send(request, cancellationToken); }
    }

    private sealed class Fixture : IDisposable
    {
        public Clock Time { get; } = new();
        public TokenProvider Tokens { get; } = new();
        public IgdbOptions Options { get; } = new() { ClientId = FakeClientId, ClientSecret = FakeSecret };
        public Handler Handler { get; }
        private readonly HttpClient http;
        public IgdbQueryCache Cache { get; }
        public IgdbRequestScheduler Scheduler { get; }
        public IgdbClient Client { get; }
        public Fixture(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? send = null)
        {
            Handler = new(send ?? ((_, _) => Task.FromResult(Json())));
            http = new HttpClient(Handler) { Timeout = Timeout.InfiniteTimeSpan };
            Cache = new(Time); Scheduler = new(Time); Client = CreateClient();
        }
        public IgdbClient CreateClient() => new(http, Tokens, Microsoft.Extensions.Options.Options.Create(Options), Cache, Scheduler, Time);
        public void Dispose() { http.Dispose(); Cache.Dispose(); Scheduler.Dispose(); }
    }

    private static void Safe(Exception error)
    {
        Assert.DoesNotContain(FakeClientId, error.ToString());
        Assert.DoesNotContain(FakeSecret, error.ToString());
        Assert.DoesNotContain(FakeToken, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task SearchRanksThirtyCandidatesBeforeLimitingAndCachesRankedResults()
    {
        using var fixture = new Fixture(async (request, _) =>
        {
            var query = await request.Content!.ReadAsStringAsync();
            Assert.Contains("limit 30;", query);
            var payload = Enumerable.Range(1, 30).Select(id => new
            {
                id, name = id == 30 ? "Orbit" : "Orbit Bundle " + id,
                game_type = new { id = id == 30 ? 0 : 3, type = id == 30 ? "Main Game" : "Bundle" }
            });
            return Json(JsonSerializer.Serialize(payload));
        });
        var games = await fixture.Client.SearchAsync("Orbit");
        Assert.Equal(10, games.Count); Assert.Equal(30, games[0].IgdbId);
        Assert.Same(games, await fixture.Client.SearchAsync("Orbit"));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task SelectedRankedIdentityIsPreservedByGetById()
    {
        using var fixture = new Fixture(async (request, _) =>
        {
            var query = await request.Content!.ReadAsStringAsync();
            return query.Contains("search")
                ? Json("[{\"id\":1,\"name\":\"Orbit Bundle\",\"game_type\":{\"id\":3}},{\"id\":974,\"name\":\"Orbit\",\"game_type\":{\"id\":0}}]")
                : Json("[{\"id\":974,\"name\":\"Orbit\",\"game_type\":{\"id\":0}}]");
        });
        var selected = (await fixture.Client.SearchAsync("Orbit"))[0];
        var details = await fixture.Client.GetByIdAsync(selected.IgdbId);
        Assert.Equal(974, selected.IgdbId); Assert.Equal(selected, details);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(0, "Main Game")]
    [InlineData(8, "Remake")]
    [InlineData(9, "Remaster")]
    public async Task AcceptsNonNegativeGameTypeIds(long typeId, string type)
    {
        var payload = JsonSerializer.Serialize(new[] { new { id = 974, name = "Resident Evil 4", game_type = new { id = typeId, type } } });
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(payload)));
        var game = Assert.Single(await fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Equal(974, game.IgdbId);
        Assert.Equal(typeId, game.GameTypeId);
        Assert.Equal(type, game.GameType);
    }

    [Fact]
    public async Task MixedMainGameRemakeAndRemasterKeepDistinctIdsAndAreCached()
    {
        var payload = JsonSerializer.Serialize(new[]
        {
            new { id = 974, name = "Resident Evil 4", game_type = new { id = 0, type = "Main Game" } },
            new { id = 132181, name = "Resident Evil 4", game_type = new { id = 8, type = "Remake" } },
            new { id = 20065, name = "Resident Evil 4", game_type = new { id = 9, type = "Remaster" } }
        });
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(payload)));
        var games = await fixture.Client.SearchAsync("Resident Evil 4");
        Assert.Equal(new long[] { 974, 132181, 20065 }, games.Select(g => g.IgdbId));
        Assert.Equal(new long?[] { 0, 8, 9 }, games.Select(g => g.GameTypeId));
        Assert.All(games, g => Assert.Equal("Resident Evil 4", g.Title));
        Assert.Same(games, await fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(0, 0, null)]
    [InlineData(-1, 0, null)]
    [InlineData(974, -1, null)]
    [InlineData(974, 0, 0L)]
    public async Task StillRejectsInvalidGameTypeAndParentIdentifiers(long gameId, long typeId, long? parentId)
    {
        var payload = JsonSerializer.Serialize(new[] { new { id = gameId, name = "Resident Evil 4", game_type = new { id = typeId, type = "Test" }, version_parent = parentId } });
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(payload)));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure);
        Assert.False(fixture.Cache.TryGet("search:RESIDENT EVIL 4", out _));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("{\"type\":\"Main Game\"}")]
    public async Task MissingOptionalGameTypeIdRemainsNull(string gameType)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("[{\"id\":974,\"name\":\"Resident Evil 4\",\"game_type\":" + gameType + "}]")));
        var game = Assert.Single(await fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Null(game.GameTypeId);
    }

    [Fact]
    public async Task GetsMainGameWithZeroTypeId()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("[{\"id\":974,\"name\":\"Resident Evil 4\",\"game_type\":{\"id\":0,\"type\":\"Main Game\"}}]")));
        var game = await fixture.Client.GetByIdAsync(974);
        Assert.NotNull(game);
        Assert.Equal(974, game.IgdbId);
        Assert.Equal("Resident Evil 4", game.Title);
        Assert.Equal(0, game.GameTypeId);
        Assert.Equal("Main Game", game.GameType);
    }

    [Fact]
    public async Task InvalidResponseDoesNotCachePartialResultsOrPreventSuccessfulRetry()
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(++calls == 1
            ? "[{\"id\":974,\"name\":\"Resident Evil 4\",\"game_type\":{\"id\":0,\"type\":\"Main Game\"}},{\"id\":0,\"name\":\"Invalid\"}]"
            : "[{\"id\":974,\"name\":\"Resident Evil 4\",\"game_type\":{\"id\":0,\"type\":\"Main Game\"}}]")));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure);
        Assert.False(fixture.Cache.TryGet("search:RESIDENT EVIL 4", out _));
        var games = await fixture.Client.SearchAsync("Resident Evil 4");
        Assert.Equal(974, Assert.Single(games).IgdbId);
        Assert.Same(games, await fixture.Client.SearchAsync("Resident Evil 4"));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task UsesPostHeadersAndOnlyRequiredFieldsAndMapsMetadata()
    {
        using var fixture = new Fixture(async (request, _) =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.igdb.com/v4/games", request.RequestUri!.AbsoluteUri);
            Assert.Equal(FakeClientId, request.Headers.GetValues("Client-ID").Single());
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.StartsWith(FakeToken, request.Headers.Authorization.Parameter);
            var query = await request.Content!.ReadAsStringAsync();
            Assert.Contains("search \"Resident Evil\";", query);
            Assert.Contains("game_type.id,game_type.type", query);
            Assert.Contains("cover.image_id", query);
            Assert.EndsWith("limit 30;", query);
            Assert.DoesNotContain("category", query);
            Assert.DoesNotContain("where version_parent", query);
            Assert.DoesNotContain("*", query);
            return Json("[{\"id\":123,\"name\":\"Resident Evil\",\"cover\":{\"image_id\":\"co_test\"},\"first_release_date\":1577836800,\"game_type\":{\"id\":8,\"type\":\"Remake\"},\"version_title\":\"Gold\",\"version_parent\":100}]");
        });
        var game = Assert.Single(await fixture.Client.SearchAsync("  Resident   Evil  "));
        Assert.Equal(123, game.IgdbId);
        Assert.Equal("Resident Evil", game.Title);
        Assert.Equal("co_test", game.CoverImageId);
        Assert.Equal(new DateOnly(2020, 1, 1), game.FirstReleaseDate);
        Assert.Equal(2020, game.ReleaseYear);
        Assert.Equal(8, game.GameTypeId);
        Assert.Equal("Remake", game.GameType);
        Assert.Equal("Gold", game.EditionTitle);
        Assert.Equal(100, game.VersionParentIgdbId);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("  ")] [InlineData("ab")]
    [InlineData("abc\nxyz")] [InlineData("abc\0xyz")] [InlineData("abc\u2028xyz")]
    [InlineData("abc\u200bxyz")]
    public async Task RejectsInvalidTermsBeforeTokenOrNetwork(string? term)
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync(term!));
        Assert.Equal(IgdbQueryFailure.InvalidInput, error.Failure); Safe(error);
        Assert.Equal(0, fixture.Handler.Calls); Assert.Equal(0, fixture.Tokens.Gets);
    }

    [Fact]
    public async Task RejectsOversizedTerms()
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync(new string('x', 101)));
        await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync(new string(' ', 1100) + "abc"));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData("Ação e aventura")]
    [InlineData("Halo \"Collection\"")]
    [InlineData("Path\\Name; fields *; limit 500;")]
    [InlineData("abc\\\"; where id > 0; //")]
    public async Task EscapesUserTextInsideOneLiteralWithoutChangingQueryStructure(string term)
    {
        using var fixture = new Fixture(async (request, _) =>
        {
            var query = await request.Content!.ReadAsStringAsync();
            Assert.StartsWith("search \"", query);
            var recovered = new StringBuilder();
            var index = "search \"".Length;
            while (index < query.Length && query[index] != '"')
            {
                if (query[index] == '\\') index++;
                recovered.Append(query[index++]);
            }
            Assert.Equal(term, recovered.ToString());
            Assert.StartsWith("\"; fields id,name,", query[index..]);
            Assert.EndsWith("limit 30;", query);
            return Json();
        });
        await fixture.Client.SearchAsync(term);
    }

    [Fact]
    public async Task PreservesApiOrderAndDistinctVersionsWithSameTitleAndCapsAtTen()
    {
        var payload = Enumerable.Range(1, 12).Reverse().Select(id => new
        { id, name = "Mesmo título", game_type = new { id = id == 12 ? 0 : 8, type = id == 12 ? "Main Game" : "Remake" }, version_title = id == 10 ? "Gold" : null, version_parent = (long?)null });
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(payload))));
        var results = await fixture.Client.SearchAsync("Mesmo título");
        Assert.Equal(10, results.Count);
        Assert.Equal(Enumerable.Range(3, 10).Reverse().Select(x => (long)x), results.Select(x => x.IgdbId));
        Assert.Equal("Main Game", results[0].GameType);
        Assert.Equal("Remake", results[1].GameType);
        Assert.Equal("Gold", results[2].EditionTitle);
    }

    [Fact]
    public async Task AllowsMissingOptionalFieldsAndDeduplicatesOnlyIdenticalIds()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("[{\"id\":2,\"name\":\"Mesmo\"},{\"id\":1,\"name\":\"Mesmo\"},{\"id\":2,\"name\":\"Mesmo\"}]")));
        var results = await fixture.Client.SearchAsync("Mesmo");
        Assert.Equal(new long[] { 2, 1 }, results.Select(x => x.IgdbId));
        Assert.All(results, game => { Assert.Null(game.CoverImageId); Assert.Null(game.FirstReleaseDate); Assert.Null(game.GameType); Assert.Null(game.EditionTitle); });
    }

    [Fact]
    public async Task EmptySearchAndAbsentIdAreValidAndCached()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("[]")));
        Assert.Empty(await fixture.Client.SearchAsync("Unknown"));
        Assert.Empty(await fixture.Client.SearchAsync("unknown"));
        Assert.Null(await fixture.Client.GetByIdAsync(999));
        Assert.Null(await fixture.Client.GetByIdAsync(999));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public async Task GetsSpecificIdWithNumericQuery()
    {
        using var fixture = new Fixture(async (request, _) =>
        {
            var query = await request.Content!.ReadAsStringAsync();
            Assert.Contains("where id = 123; limit 1;", query);
            Assert.DoesNotContain("search", query);
            return Json();
        });
        Assert.Equal(123, (await fixture.Client.GetByIdAsync(123))!.IgdbId);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public async Task RejectsInvalidIdWithoutNetwork(long id)
    {
        using var fixture = new Fixture();
        await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.GetByIdAsync(id));
        Assert.Equal(0, fixture.Handler.Calls);
    }

    [Fact]
    public async Task RejectsWrongIdFromApi()
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.GetByIdAsync(999));
        Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure);
    }

    [Theory]
    [InlineData("{}")] [InlineData("null")] [InlineData("invalid")]
    [InlineData("[null]")] [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"name\":\"Test\"}]")]
    [InlineData("[{\"id\":1,\"name\":\" \"}]")]
    [InlineData("[{\"id\":1,\"name\":\"Test\",\"first_release_date\":9223372036854775807}]")]
    [InlineData("[{\"id\":1,\"name\":\"Test\",\"version_parent\":-1}]")]
    [InlineData("[{\"id\":1,\"name\":\"Test\",\"cover\":{\"image_id\":\"https://evil.example/image\"}}]")]
    public async Task RejectsInvalidResponsesWithoutLeakingBody(string json)
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(json)));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure); Safe(error);
    }

    [Fact]
    public async Task LimitsResponseBytesAndTextLengths()
    {
        foreach (var json in new[] { new string('x', 262145), JsonSerializer.Serialize(new[] { new { id = 1, name = new string('x', 301) } }) })
        {
            using var fixture = new Fixture((_, _) => Task.FromResult(Json(json)));
            var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
            Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure);
        }
    }

    [Fact]
    public async Task UnauthorizedInvalidatesRejectedTokenAndRetriesOnlyOnce()
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(++calls == 1 ? Json("private", HttpStatusCode.Unauthorized) : Json()));
        Assert.Single(await fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(2, calls); Assert.Single(fixture.Tokens.Invalidated);
        using var denied = new Fixture((_, _) => Task.FromResult(Json(FakeToken + FakeSecret, HttpStatusCode.Unauthorized)));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => denied.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.Unauthorized, error.Failure); Safe(error);
        Assert.Equal(2, denied.Handler.Calls); Assert.Equal(2, denied.Tokens.Invalidated.Count);
    }

    [Fact]
    public async Task RateLimitWaitsForRetryAfterAndRetriesOnce()
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) =>
        {
            var response = ++calls == 1 ? Json("private", HttpStatusCode.TooManyRequests) : Json();
            if (calls == 1) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(2));
            return Task.FromResult(response);
        });
        var before = fixture.Time.GetUtcNow();
        Assert.Single(await fixture.Client.SearchAsync("Jogo"));
        Assert.True(fixture.Time.GetUtcNow() - before >= TimeSpan.FromSeconds(2));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task LongRetryAfterBlocksFollowingCallsWithoutSleepingIndefinitely()
    {
        using var fixture = new Fixture((_, _) =>
        {
            var response = Json("private", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromHours(1));
            return Task.FromResult(response);
        });
        for (var i = 0; i < 2; i++)
        {
            var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
            Assert.Equal(IgdbQueryFailure.RateLimited, error.Failure);
            Assert.True(error.RetryAfter > TimeSpan.FromSeconds(5)); Safe(error);
        }
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Fact]
    public async Task RepeatedRateLimitHasFiniteAttempts()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json(FakeToken, HttpStatusCode.TooManyRequests)));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.RateLimited, error.Failure); Safe(error);
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(400)] [InlineData(403)] [InlineData(500)] [InlineData(503)] [InlineData(302)]
    public async Task HttpErrorsAreNotEmptyResultsAndAreNotCached(int status)
    {
        var calls = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(++calls == 1 ? Json(FakeSecret + FakeToken + FakeClientId, (HttpStatusCode)status) : Json()));
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.HttpError, error.Failure);
        Assert.Equal((HttpStatusCode)status, error.StatusCode); Safe(error);
        Assert.Single(await fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task CacheNormalizesKeysIsSharedAndExpiresAfterFiveMinutes()
    {
        using var fixture = new Fixture();
        var first = await fixture.Client.SearchAsync("  Ação   Heroica ");
        Assert.Same(first, await fixture.CreateClient().SearchAsync("ação heroica"));
        Assert.Equal(1, fixture.Handler.Calls);
        fixture.Time.Advance(TimeSpan.FromMinutes(5));
        Assert.NotSame(first, await fixture.Client.SearchAsync("Ação Heroica"));
        Assert.Equal(2, fixture.Handler.Calls);
    }

    [Fact]
    public void CacheIsBoundedAndContainsOnlyTypedResults()
    {
        using var fixture = new Fixture();
        for (var i = 0; i < 500; i++) fixture.Cache.Set($"search:{i}", Array.AsReadOnly(new[] { new IgdbGameResult(i + 1, "Jogo", null, null, null, null, null, null) }));
        var stored = Enumerable.Range(0, 500).Count(i => fixture.Cache.TryGet($"search:{i}", out _));
        Assert.InRange(stored, 1, 100);
    }

    [Fact]
    public async Task SharedSchedulerSpacesCallsAndAllowsOnlyOneActiveRequest()
    {
        var starts = new List<long>(); var active = 0; var maximum = 0;
        Fixture? fixture = null;
        fixture = new Fixture(async (_, ct) =>
        {
            maximum = Math.Max(maximum, Interlocked.Increment(ref active));
            starts.Add(fixture!.Time.GetTimestamp());
            await Task.Yield(); ct.ThrowIfCancellationRequested();
            Interlocked.Decrement(ref active); return Json();
        });
        using (fixture)
        {
            await Task.WhenAll(Enumerable.Range(0, 8).Select(i => fixture.CreateClient().SearchAsync($"Jogo {i}")));
            Assert.Equal(1, maximum);
            Assert.Equal(8, starts.Count);
            Assert.All(starts.Zip(starts.Skip(1), (a, b) => b - a), elapsed => Assert.True(elapsed >= IgdbRequestScheduler.MinimumSpacing.Ticks));
        }
    }

    [Fact]
    public async Task TimeoutAndTransportErrorsAreSafe()
    {
        using var timeout = new Fixture((_, _) => throw new TaskCanceledException(FakeToken + FakeSecret));
        var timeoutError = await Assert.ThrowsAsync<IgdbQueryException>(() => timeout.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.Timeout, timeoutError.Failure); Safe(timeoutError);
        using var network = new Fixture((_, _) => throw new HttpRequestException(FakeToken + FakeSecret));
        var networkError = await Assert.ThrowsAsync<IgdbQueryException>(() => network.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.Unavailable, networkError.Failure); Safe(networkError);
    }

    [Fact]
    public async Task RealPerAttemptTimeoutIsEnforced()
    {
        using var fixture = new Fixture(async (_, ct) => { await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(); });
        fixture.Options.TimeoutSeconds = 1;
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.Timeout, error.Failure);
    }

    [Fact]
    public async Task CancellationBeforeAndDuringRequestIsPropagated()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, ct); return Json(); });
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Client.SearchAsync("Jogo", canceled.Token));
        Assert.Equal(0, fixture.Handler.Calls);
        using var cancellation = new CancellationTokenSource();
        var task = fixture.Client.SearchAsync("Jogo", cancellation.Token);
        await started.Task; cancellation.Cancel();
        Safe(await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task));
    }

    [Theory]
    [InlineData("disabled")] [InlineData("missing")]
    public async Task UnconfiguredIntegrationNeverCallsNetwork(string condition)
    {
        using var fixture = new Fixture();
        if (condition == "disabled") fixture.Options.Enabled = false; else fixture.Tokens.IsConfigured = false;
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(condition == "disabled" ? IgdbQueryFailure.Disabled : IgdbQueryFailure.NotConfigured, error.Failure);
        Safe(error); Assert.Equal(0, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("https://evil.example/image")]
    [InlineData("../image")] [InlineData("a?secret=value")]
    public void InvalidCoverIdsUseNoUrl(string? imageId) => Assert.Null(IgdbCoverImages.GetUrl(imageId));

    [Fact]
    public void BuildsOnlyDocumentedCdnSizes()
    {
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_small/co_test.jpg", IgdbCoverImages.GetUrl("co_test")!.AbsoluteUri);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co_test.jpg", IgdbCoverImages.GetUrl("co_test", IgdbCoverSize.Big)!.AbsoluteUri);
        Assert.Throws<ArgumentOutOfRangeException>(() => IgdbCoverImages.GetUrl("co_test", (IgdbCoverSize)99));
    }

    [Fact]
    public void RegistrationKeepsAuthenticationSingleAndSharesCacheAndScheduler()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection().AddLogging().AddIgdbAuthentication(configuration).AddIgdbSearch();
        Assert.Single(services, x => x.ServiceType == typeof(IIgdbTokenProvider));
        using var root = services.BuildServiceProvider(validateScopes: true); using var scope = root.CreateScope();
        Assert.NotSame(root.GetRequiredService<IIgdbClient>(), scope.ServiceProvider.GetRequiredService<IIgdbClient>());
        Assert.Same(root.GetRequiredService<IgdbQueryCache>(), scope.ServiceProvider.GetRequiredService<IgdbQueryCache>());
        Assert.Same(root.GetRequiredService<IgdbRequestScheduler>(), scope.ServiceProvider.GetRequiredService<IgdbRequestScheduler>());
    }

    [Fact]
    public async Task SearchDoesNotWriteToLocalLibrary()
    {
        using var handler = new Handler((_, _) => Task.FromResult(Json()));
        using var baseApp = new LibraryApplication();
        using var app = baseApp.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.Configure<IgdbOptions>(options => { options.Enabled = true; options.ClientId = FakeClientId; options.ClientSecret = FakeSecret; });
            services.RemoveAll<IIgdbTokenProvider>(); services.AddSingleton<IIgdbTokenProvider>(new TokenProvider());
            services.AddHttpClient<IIgdbClient, IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => handler);
        }));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var id = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
        { Title = "Biblioteca preservada", Rating = 7, Notes = "Manter", SelectedPlatforms = ["pc"] });
        Assert.Single(await scope.ServiceProvider.GetRequiredService<IIgdbClient>().SearchAsync("Jogo"));
        Assert.Single(await db.Games.ToListAsync()); Assert.Single(await db.LibraryEntries.ToListAsync());
        Assert.Single(await db.GameOwnerships.ToListAsync());
        Assert.Equal(7, (await db.LibraryEntries.FindAsync(id))!.Rating);
        Assert.Equal("Manter", (await db.LibraryEntries.FindAsync(id))!.Notes);
        Assert.Equal("Biblioteca preservada", (await db.Games.SingleAsync()).Title);
    }

    private sealed class AuthFactory(Handler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name)
        {
            Assert.Equal(IgdbTokenProvider.HttpClientName, name);
            return new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RealTokenProviderIsReusedAndConditionallyRenewedWithSimulatedHttp(bool rejectFirst)
    {
        var apiCalls = 0; var authentications = 0;
        using var fixture = new Fixture((_, _) => Task.FromResult(++apiCalls == 1 && rejectFirst ? Json("private", HttpStatusCode.Unauthorized) : Json()));
        using var authHandler = new Handler((_, _) => Task.FromResult(Json(JsonSerializer.Serialize(new
        { access_token = $"synthetic-token-{++authentications}", expires_in = 3600, token_type = "bearer" }))));
        using var provider = new IgdbTokenProvider(new AuthFactory(authHandler), Options.Create(fixture.Options), fixture.Time);
        using var http = new HttpClient(fixture.Handler, disposeHandler: false);
        var client = new IgdbClient(http, provider, Options.Create(fixture.Options), fixture.Cache, fixture.Scheduler, fixture.Time);
        await client.SearchAsync("Jogo primeiro");
        await client.SearchAsync("Jogo segundo");
        Assert.Equal(rejectFirst ? 2 : 1, authHandler.Calls);
        Assert.Equal(rejectFirst ? 3 : 2, apiCalls);
    }

    [Fact]
    public async Task RateLimitAcceptsHttpDateAndCombinedRetriesRemainBounded()
    {
        var calls = 0;
        Fixture? fixture = null;
        fixture = new Fixture((_, _) =>
        {
            var response = ++calls switch
            {
                1 => Json("private", HttpStatusCode.Unauthorized),
                2 => Json("private", HttpStatusCode.TooManyRequests),
                _ => Json()
            };
            if (calls == 2) response.Headers.RetryAfter = new RetryConditionHeaderValue(fixture!.Time.GetUtcNow().AddSeconds(2));
            return Task.FromResult(response);
        });
        using (fixture)
        {
            Assert.Single(await fixture.Client.SearchAsync("Jogo"));
            Assert.Equal(3, calls);
            Assert.Single(fixture.Tokens.Invalidated);
        }
    }

    [Fact]
    public async Task EnforcesStreamingSizeLimitWhenContentLengthIsIncorrect()
    {
        using var fixture = new Fixture((_, _) =>
        {
            var response = Json(new string('x', 262145));
            response.Content.Headers.ContentLength = 1;
            return Task.FromResult(response);
        });
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.InvalidResponse, error.Failure);
    }

    [Fact]
    public async Task EmptyOptionalStringsAreTreatedAsMissing()
    {
        using var fixture = new Fixture((_, _) => Task.FromResult(Json("[{\"id\":1,\"name\":\"Jogo\",\"cover\":{\"image_id\":\"\"},\"game_type\":{\"type\":\"\"},\"version_title\":\"\"}]")));
        var game = Assert.Single(await fixture.Client.SearchAsync("Jogo"));
        Assert.Null(game.CoverImageId); Assert.Null(game.GameType); Assert.Null(game.EditionTitle);
    }

    [Fact]
    public async Task CancelingQueuedRequestNeverSendsIt()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(async (_, ct) => { started.TrySetResult(); await release.Task.WaitAsync(ct); return Json(); });
        var first = fixture.Client.SearchAsync("Jogo primeiro");
        await started.Task;
        using var cancellation = new CancellationTokenSource();
        var second = fixture.CreateClient().SearchAsync("Jogo segundo", cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        release.SetResult(); await first;
        Assert.Equal(1, fixture.Handler.Calls);
    }

    [Theory]
    [InlineData(0)] [InlineData(61)]
    public async Task InvalidTimeoutIsRejectedWithoutNetwork(int timeout)
    {
        using var fixture = new Fixture(); fixture.Options.TimeoutSeconds = timeout;
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => fixture.Client.SearchAsync("Jogo"));
        Assert.Equal(IgdbQueryFailure.InvalidConfiguration, error.Failure);
        Assert.Equal(0, fixture.Handler.Calls);
    }
}
