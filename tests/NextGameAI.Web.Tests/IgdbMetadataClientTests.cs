using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using NextGameAI.Web.Configuration;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class IgdbMetadataClientTests
{
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


    private sealed class Tokens : IIgdbTokenProvider
    {
        public bool IsConfigured => true;
        public int Invalidated;
        public Task<string> GetTokenAsync(CancellationToken ct = default) => Task.FromResult("synthetic-token");
        public void Invalidate(string rejected) => Invalidated++;
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => send(request, ct);
    }

    private sealed class Fixture : IDisposable
    {
        public string Game = "[{\"id\":974}]", Time = "[]", Multiplayer = "[]";
        public Clock Clock { get; } = new();
        public Tokens Tokens { get; } = new();
        public List<(string Endpoint, string Query, long Started)> Requests { get; } = [];
        public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? Override;
        public HttpClient Http { get; }
        public IgdbRequestScheduler Scheduler { get; }
        public IgdbApiTransport Transport { get; }
        public IIgdbMetadataClient Client { get; }
        public Fixture()
        {
            Http = new(new Handler(async (request, ct) =>
            {
                Requests.Add((request.RequestUri!.AbsolutePath, await request.Content!.ReadAsStringAsync(ct), Clock.GetTimestamp()));
                Assert.Equal("https", request.RequestUri.Scheme);
                Assert.Equal("api.igdb.com", request.RequestUri.Host);
                if (Override is not null) return await Override(request, ct);
                return Json(request.RequestUri.AbsolutePath switch
                {
                    "/v4/games" => Game,
                    "/v4/game_time_to_beats" => Time,
                    "/v4/multiplayer_modes" => Multiplayer,
                    _ => throw new InvalidOperationException("Unexpected endpoint")
                });
            })) { Timeout = Timeout.InfiniteTimeSpan };
            Scheduler = new(Clock);
            Transport = new(Http, Tokens, Options.Create(new IgdbOptions { ClientId = "synthetic", ClientSecret = "synthetic" }), Scheduler, Clock);
            Client = new IgdbMetadataClient(Transport);
        }
        public void Dispose() { Http.Dispose(); Scheduler.Dispose(); }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task ExactQueryExpandsAllFiveKindsAndKeepsExternalIdentity()
    {
        using var f = new Fixture
        {
            Game = """[{"id":974,"genres":[{"id":10,"name":"Genre"}],"themes":[{"id":10,"name":"Theme"}],"game_modes":[{"id":10,"name":"Mode"}],"player_perspectives":[{"id":10,"name":"Perspective"}],"keywords":[{"id":10,"name":"synthetic-spoiler"}]}]"""
        };
        var r = await f.Client.GetByIdAsync(974);
        var sections = new[] { r.Genres, r.Themes, r.GameModes, r.PlayerPerspectives, r.Keywords };
        Assert.All(sections, s => { Assert.Equal(IgdbMetadataStatus.Success, s.Status); Assert.Equal(10, Assert.Single(s.Value!).IgdbId); });
        Assert.Equal(Enum.GetValues<IgdbTaxonomyKind>(), sections.Select(s => s.Value![0].Kind));
        Assert.Equal(974, r.IgdbId);
        var query = f.Requests[0].Query;
        foreach (var field in new[] { "genres", "themes", "game_modes", "player_perspectives", "keywords" })
        { Assert.Contains($"{field}.id", query); Assert.Contains($"{field}.name", query); }
        Assert.Contains("where id = 974;", query); Assert.DoesNotContain("search", query);
        Assert.Equal(new[] { "/v4/games", "/v4/game_time_to_beats", "/v4/multiplayer_modes" }, f.Requests.Select(x => x.Endpoint));
        Assert.Contains("where game_id = 974;", f.Requests[1].Query);
        Assert.Contains("where game = 974;", f.Requests[2].Query);
        Assert.Contains("platform.id,platform.name", f.Requests[2].Query);
        Assert.DoesNotContain("synthetic-spoiler", r.ToString());
        Assert.DoesNotContain("synthetic-spoiler", r.Keywords.ToString());
        Assert.DoesNotContain("synthetic-spoiler", r.Keywords.Value![0].ToString());
        Assert.DoesNotContain(typeof(CatalogGameResult).GetProperties(), p => p.Name.Contains("Keyword", StringComparison.OrdinalIgnoreCase));
        Assert.Throws<NotSupportedException>(() => ((IList<IgdbTaxonomyResult>)r.Genres.Value!).Clear());
    }

    [Theory]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":0,\"name\":\"x\"}]")]
    [InlineData("[{\"id\":-1,\"name\":\"x\"}]")]
    [InlineData("[{\"id\":1}]")]
    [InlineData("[{\"id\":1,\"name\":\" \"}]")]
    [InlineData("[1]")]
    [InlineData("[null]")]
    [InlineData("{}")]
    [InlineData("[{\"id\":1,\"name\":5}]")]
    [InlineData("[{\"id\":1,\"name\":\"x\"},{\"id\":1,\"name\":\"x\"}]")]
    [InlineData("[{\"id\":1,\"name\":\"x\"},{\"id\":1,\"name\":\"conflict\"}]")]
    public async Task InvalidTaxonomyRejectsWholeSectionButPreservesIndependentSections(string section)
    {
        using var f = new Fixture { Game = "[{\"id\":974,\"genres\":" + section + ",\"themes\":[{\"id\":1,\"name\":\"Valid\"}]}]" };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, r.Genres.Status); Assert.Null(r.Genres.Value);
        Assert.Equal(IgdbMetadataStatus.Success, r.Themes.Status);
        Assert.Equal(IgdbMetadataStatus.Empty, r.TimeToBeat.Status);
    }

    [Theory]
    [InlineData(500, true)] [InlineData(501, false)]
    public async Task TaxonomyNamesRespectModelLimit(int length, bool valid)
    {
        using var f = new Fixture { Game = JsonSerializer.Serialize(new[] { new { id = 974, genres = new[] { new { id = 1, name = new string('x', length) } } } }) };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(valid ? IgdbMetadataStatus.Success : IgdbMetadataStatus.InvalidResponse, r.Genres.Status);
    }

    [Theory]
    [InlineData("[{\"id\":974}]")]
    [InlineData("[{\"id\":974,\"genres\":null,\"keywords\":[]}]")]
    public async Task OptionalAbsentAndEmptySectionsAreSuccessfulEmpty(string game)
    {
        using var f = new Fixture { Game = game };
        var r = await f.Client.GetByIdAsync(974);
        Assert.All(new[] { r.Genres, r.Themes, r.GameModes, r.PlayerPerspectives, r.Keywords }, s =>
        { Assert.Equal(IgdbMetadataStatus.Empty, s.Status); Assert.Empty(s.Value!); });
        Assert.Equal(IgdbMetadataStatus.Empty, r.TimeToBeat.Status);
        Assert.Equal(IgdbMetadataStatus.Empty, r.Multiplayer.Status);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[null]")]
    [InlineData("[{\"id\":132181}]")]
    [InlineData("[{}]")]
    [InlineData("[{\"id\":974},{\"id\":974}]")]
    [InlineData("[{\"id\":974,\"id\":132181}]")]
    [InlineData("[{\"id\":974")]
    public async Task InvalidGameEnvelopeNeverBecomesNotFoundOrPartialSuccess(string game)
    {
        using var f = new Fixture { Game = game };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, r.Genres.Status);
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, r.TimeToBeat.Status);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task GameNotFoundSkipsComplementaryQueries()
    {
        using var f = new Fixture { Game = "[]" };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.GameNotFound, r.Genres.Status);
        Assert.Equal(IgdbMetadataStatus.GameNotFound, r.TimeToBeat.Status);
        Assert.Equal(IgdbMetadataStatus.GameNotFound, r.Multiplayer.Status);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(long.MinValue)]
    public async Task InvalidIdNeverRequestsTokenOrHttp(long id)
    {
        using var f = new Fixture();
        var error = await Assert.ThrowsAsync<IgdbQueryException>(() => f.Client.GetByIdAsync(id));
        Assert.Equal(IgdbQueryFailure.InvalidInput, error.Failure); Assert.Empty(f.Requests);
    }

    [Fact]
    public async Task DurationKeepsSecondsZeroNullableFieldsCountAndSource()
    {
        using var f = new Fixture { Time = """[{"id":4,"game_id":974,"hastily":0,"normally":72669,"count":17}]""" };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.Success, r.TimeToBeat.Status);
        var time = r.TimeToBeat.Value!;
        Assert.Equal(0, time.HastilySeconds); Assert.Equal(72669, time.NormallySeconds);
        Assert.Null(time.CompletelySeconds); Assert.Equal(17, time.SubmissionCount);
        Assert.Equal(974, time.GameIgdbId); Assert.Equal(4, time.IgdbId); Assert.Equal("IGDB", time.Source);
    }

    [Fact]
    public async Task DurationRecordWithNoEstimatesPreservesNulls()
    {
        using var f = new Fixture { Time = """[{"id":4,"game_id":974}]""" };
        var time = (await f.Client.GetByIdAsync(974)).TimeToBeat.Value!;
        Assert.Null(time.HastilySeconds); Assert.Null(time.NormallySeconds);
        Assert.Null(time.CompletelySeconds); Assert.Null(time.SubmissionCount);
    }

    [Theory]
    [InlineData("[{\"id\":1,\"game_id\":974,\"hastily\":-1}]")]
    [InlineData("[{\"id\":1,\"game_id\":974,\"count\":-1}]")]
    [InlineData("[{\"id\":1,\"game_id\":974,\"count\":2147483648}]")]
    [InlineData("[{\"id\":1,\"game_id\":974,\"normally\":1.5}]")]
    [InlineData("[{\"id\":1,\"game_id\":132181}]")]
    [InlineData("[{\"id\":1}]")]
    [InlineData("[{\"game_id\":974}]")]
    [InlineData("[{\"id\":1,\"game_id\":974},{\"id\":2,\"game_id\":974}]")]
    [InlineData("[null]")]
    public async Task InvalidDurationDoesNotPoisonOtherSections(string time)
    {
        using var f = new Fixture { Time = time };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, r.TimeToBeat.Status); Assert.Null(r.TimeToBeat.Value);
        Assert.Equal(IgdbMetadataStatus.Empty, r.Multiplayer.Status);
    }

    [Fact]
    public async Task MultiplayerKeepsExactPlatformsAndTrueFalseNullAndZero()
    {
        using var f = new Fixture { Multiplayer = """[{"id":1,"game":974,"platform":{"id":48,"name":"PlayStation 4"},"onlinecoop":true,"offlinecoop":false,"onlinecoopmax":0,"campaigncoop":true,"dropin":false,"lancoop":true,"splitscreen":false,"splitscreenonline":true,"offlinecoopmax":2,"offlinemax":4,"onlinemax":8},{"id":2,"game":974,"platform":{"id":167,"name":"PlayStation 5"}}]""" };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.Success, r.Multiplayer.Status);
        var values = r.Multiplayer.Value!;
        Assert.Equal(new long[] { 48, 167 }, values.Select(x => x.Platform!.IgdbId));
        Assert.True(values[0].OnlineCoop); Assert.False(values[0].OfflineCoop); Assert.Equal(0, values[0].OnlineCoopMax);
        Assert.True(values[0].CampaignCoop); Assert.False(values[0].DropIn); Assert.True(values[0].LanCoop);
        Assert.False(values[0].SplitScreen); Assert.True(values[0].SplitScreenOnline);
        Assert.Equal(2, values[0].OfflineCoopMax); Assert.Equal(4, values[0].OfflineMax); Assert.Equal(8, values[0].OnlineMax);
        Assert.Null(values[1].OnlineCoop); Assert.Null(values[1].OfflineMax);
        Assert.Equal("IGDB", values[0].Source);
    }

    [Fact]
    public async Task SingleUnknownPlatformStaysUnknown()
    {
        using var f = new Fixture { Multiplayer = """[{"id":1,"game":974}]""" };
        var value = Assert.Single((await f.Client.GetByIdAsync(974)).Multiplayer.Value!);
        Assert.Null(value.Platform); Assert.Null(value.OnlineCoop);
    }

    [Theory]
    [InlineData("[{\"id\":1,\"game\":974,\"platform\":48}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"platform\":{\"id\":48}}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"platform\":{\"id\":0,\"name\":\"x\"}}]")]
    [InlineData("[{\"id\":1,\"game\":132181}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"onlinecoop\":1}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"onlinemax\":-1}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"onlinemax\":2147483648}]")]
    [InlineData("[{\"id\":1,\"game\":974},{\"id\":2,\"game\":974}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"platform\":{\"id\":6,\"name\":\"PC\"}},{\"id\":2,\"game\":974,\"platform\":{\"id\":6,\"name\":\"PC\"}}]")]
    [InlineData("[{\"id\":1,\"game\":974,\"platform\":{\"id\":6,\"name\":\"PC\"}},{\"id\":1,\"game\":974,\"platform\":{\"id\":48,\"name\":\"PS4\"}}]")]
    public async Task AmbiguousOrInvalidMultiplayerIsNeverSilentlyMerged(string multiplayer)
    {
        using var f = new Fixture { Multiplayer = multiplayer };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, r.Multiplayer.Status); Assert.Null(r.Multiplayer.Value);
        Assert.Equal(IgdbMetadataStatus.Empty, r.Genres.Status);
    }

    [Fact]
    public async Task FullMultiplayerPageIsRejectedAsPossiblyIncomplete()
    {
        using var f = new Fixture { Multiplayer = JsonSerializer.Serialize(Enumerable.Range(1, 100).Select(i => new { id = i, game = 974, platform = new { id = i, name = "Platform" } })) };
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, (await f.Client.GetByIdAsync(974)).Multiplayer.Status);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)] [InlineData(500)] [InlineData(503)]
    public async Task TransportFailureRetainsTypedStatusAndNeverIncludesBody(int status)
    {
        using var f = new Fixture();
        f.Override = (_, _) => Task.FromResult(Json("synthetic-sensitive-body", (HttpStatusCode)status));
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.TransportFailure, r.Genres.Status);
        Assert.Equal((HttpStatusCode)status, r.Genres.StatusCode);
        Assert.Equal(status is 401 or 429 ? 2 : 1, f.Requests.Count);
        Assert.Equal(status == 401 ? 2 : 0, f.Tokens.Invalidated);
        Assert.DoesNotContain("synthetic-sensitive-body", r.ToString());
    }

    [Fact]
    public async Task LongRetryAfterIsPreservedAndNotRetriedEarly()
    {
        using var f = new Fixture();
        f.Override = (_, _) =>
        {
            var response = Json("sensitive", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.FromSeconds(10));
            return Task.FromResult(response);
        };
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbQueryFailure.RateLimited, r.Genres.Failure);
        Assert.Equal(TimeSpan.FromSeconds(10), r.Genres.RetryAfter);
        Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RelativeAndAbsoluteRetryAfterReachCooldownWithoutEarlyRetry(bool absolute)
    {
        using var f = new Fixture(); var start = f.Clock.GetUtcNow();
        f.Override = (_, _) =>
        {
            var response = Json("synthetic", HttpStatusCode.TooManyRequests);
            response.Headers.RetryAfter = absolute ? new RetryConditionHeaderValue(start.AddHours(2)) : new RetryConditionHeaderValue(TimeSpan.FromHours(2));
            return Task.FromResult(response);
        };
        var external = await f.Client.GetSectionsAsync(974, new[] { IgdbMetadataSectionKind.Genres });
        Assert.Single(f.Requests); Assert.Equal(TimeSpan.FromHours(2), external.Genres.RetryAfter);
        var policy = new NextGameAI.Web.Services.GameEnrichmentCooldown(f.Clock, new(f.Clock));
        policy.RecordResult(1, start.UtcDateTime, new(NextGameAI.Web.Models.CatalogMetadataSection.Genres,
            NextGameAI.Web.Services.TaxonomyEnrichmentOutcome.ExternalFailure, external.Genres.Status,
            external.Genres.Failure, external.Genres.StatusCode, external.Genres.RetryAfter));
        var decision = policy.Evaluate(1, NextGameAI.Web.Models.CatalogMetadataSection.Genres, null);
        Assert.Equal(start.AddHours(2).UtcDateTime, decision.NextAllowedAtUtc);
        Assert.Single(f.Requests);
    }

    [Fact]
    public async Task FailureOfTimeEndpointStillAllowsMultiplayerQuery()
    {
        using var f = new Fixture();
        f.Override = (request, _) => Task.FromResult(request.RequestUri!.AbsolutePath == "/v4/games"
            ? Json(f.Game) : request.RequestUri.AbsolutePath == "/v4/game_time_to_beats"
                ? Json("sensitive", HttpStatusCode.ServiceUnavailable) : Json("[]"));
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(IgdbMetadataStatus.Empty, r.Genres.Status);
        Assert.Equal(IgdbMetadataStatus.TransportFailure, r.TimeToBeat.Status);
        Assert.Equal(IgdbMetadataStatus.Empty, r.Multiplayer.Status);
    }

    [Theory]
    [InlineData("timeout", IgdbQueryFailure.Timeout)]
    [InlineData("network", IgdbQueryFailure.Unavailable)]
    public async Task TimeoutAndNetworkErrorsRemainTypedAndSanitized(string error, IgdbQueryFailure failure)
    {
        using var f = new Fixture();
        f.Override = (_, _) => throw (error == "timeout" ? new OperationCanceledException("sensitive") : new HttpRequestException("sensitive"));
        var r = await f.Client.GetByIdAsync(974);
        Assert.Equal(failure, r.Genres.Failure); Assert.Equal(IgdbMetadataStatus.TransportFailure, r.Genres.Status);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CallerCancellationPropagatesWithoutReturningEmpty(bool during)
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource();
        if (!during) cts.Cancel();
        else f.Override = (_, ct) => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Json("[]")); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Client.GetByIdAsync(974, cts.Token));
        Assert.Equal(during ? 1 : 0, f.Requests.Count);
    }

    [Theory]
    [InlineData("oversize")]
    [InlineData("depth")]
    public async Task ExistingTransportSizeAndJsonDepthLimitsArePreserved(string kind)
    {
        using var f = new Fixture { Game = kind == "oversize" ? new string('x', 256 * 1024 + 1) : new string('[', 17) + "0" + new string(']', 17) };
        Assert.Equal(IgdbMetadataStatus.InvalidResponse, (await f.Client.GetByIdAsync(974)).Genres.Status);
    }

    [Fact]
    public async Task RepeatedMetadataQueriesHaveNoCacheAndShareSchedulerWithBasicSearch()
    {
        using var f = new Fixture(); using var cache = new IgdbQueryCache(f.Clock);
        var basic = new IgdbClient(f.Transport, cache);
        f.Game = """[{"id":974,"name":"Synthetic game"}]""";
        Assert.Single(await basic.SearchAsync("Synthetic"));
        await f.Client.GetByIdAsync(974); await f.Client.GetByIdAsync(974);
        Assert.Single(await basic.SearchAsync("Synthetic"));
        Assert.Equal(7, f.Requests.Count);
        Assert.All(f.Requests.Zip(f.Requests.Skip(1), (a, b) => b.Started - a.Started), elapsed => Assert.True(elapsed >= IgdbRequestScheduler.MinimumSpacing.Ticks));
    }

    [Fact]
    public void RegistrationUsesExistingTransportAndOneSchedulerWithoutStartingHost()
    {
        var services = new ServiceCollection().AddLogging().AddIgdbAuthentication(new ConfigurationBuilder().Build()).AddIgdbSearch();
        Assert.Single(services, d => d.ServiceType == typeof(IgdbRequestScheduler));
        Assert.Single(services, d => d.ServiceType == typeof(IIgdbTokenProvider));
        Assert.Single(services, d => d.ServiceType == typeof(IgdbApiTransport));
        Assert.Single(services, d => d.ServiceType == typeof(IIgdbMetadataClient));
        using var provider = services.BuildServiceProvider(validateScopes: true);
        Assert.IsType<IgdbMetadataClient>(provider.GetRequiredService<IIgdbMetadataClient>());
        Assert.IsType<IgdbClient>(provider.GetRequiredService<IIgdbClient>());
        Assert.DoesNotContain(typeof(IgdbMetadataClient).GetConstructors().SelectMany(c => c.GetParameters()), p => p.ParameterType.Name.Contains("DbContext"));
    }
    [Theory]
    [InlineData("Genres", 1, "id,genres.id,genres.name")]
    [InlineData("Genres,Themes", 1, "id,genres.id,genres.name,themes.id,themes.name")]
    [InlineData("TimeToBeat", 2, "id")]
    [InlineData("Multiplayer", 2, "id")]
    [InlineData("Genres,TimeToBeat", 2, "id,genres.id,genres.name")]
    [InlineData("TimeToBeat,Multiplayer", 3, "id")]
    public async Task SelectiveQueriesUseOnlyRequestedProjectionsAndEndpoints(string selection, int requests, string fields)
    {
        using var f = new Fixture { Game = """[{"id":974,"genres":[{"id":1,"name":"Synthetic"}],"themes":[],"keywords":[{"id":2,"name":"synthetic-not-requested"}]}]""" };
        var sections = selection.Split(',').Select(Enum.Parse<IgdbMetadataSectionKind>).ToArray();
        var result = await f.Client.GetSectionsAsync(974, sections);
        Assert.Equal(requests, f.Requests.Count); Assert.Equal($"fields {fields}; where id = 974; limit 2;", f.Requests[0].Query);
        Assert.Equal(sections.Contains(IgdbMetadataSectionKind.TimeToBeat), f.Requests.Any(r => r.Endpoint == "/v4/game_time_to_beats"));
        Assert.Equal(sections.Contains(IgdbMetadataSectionKind.Multiplayer), f.Requests.Any(r => r.Endpoint == "/v4/multiplayer_modes"));
        Assert.Equal(IgdbMetadataStatus.NotRequested, result.Keywords.Status); Assert.Null(result.Keywords.Value);
        Assert.Equal(IgdbMetadataStatus.NotRequested, result.GameModes.Status);
        if (!sections.Contains(IgdbMetadataSectionKind.Genres)) Assert.Equal(IgdbMetadataStatus.NotRequested, result.Genres.Status);
        if (!sections.Contains(IgdbMetadataSectionKind.TimeToBeat)) Assert.Equal(IgdbMetadataStatus.NotRequested, result.TimeToBeat.Status);
        if (!sections.Contains(IgdbMetadataSectionKind.Multiplayer)) Assert.Equal(IgdbMetadataStatus.NotRequested, result.Multiplayer.Status);
        Assert.DoesNotContain("keywords", f.Requests[0].Query);
    }

    [Fact]
    public async Task NoSelectedSectionsMakeNoHttpCallAndReturnOnlyNotRequested()
    {
        using var f = new Fixture(); var r = await f.Client.GetSectionsAsync(974, Array.Empty<IgdbMetadataSectionKind>());
        Assert.Empty(f.Requests);
        Assert.All(new[] { r.Genres, r.Themes, r.GameModes, r.PlayerPerspectives, r.Keywords }, s => { Assert.Equal(IgdbMetadataStatus.NotRequested, s.Status); Assert.Null(s.Value); });
        Assert.Equal(IgdbMetadataStatus.NotRequested, r.TimeToBeat.Status); Assert.Equal(IgdbMetadataStatus.NotRequested, r.Multiplayer.Status);
    }

    [Theory]
    [InlineData("[]", IgdbMetadataStatus.GameNotFound)]
    [InlineData("[{\"id\":132181}]", IgdbMetadataStatus.InvalidResponse)]
    [InlineData("[{\"id\":974,\"genres\":[{}]}]", IgdbMetadataStatus.InvalidResponse)]
    [InlineData("[{\"id\":974,\"genres\":[]}]", IgdbMetadataStatus.Empty)]
    public async Task SelectiveQueryKeepsRequestedFailureOrEmptyDistinctFromNotRequested(string game, IgdbMetadataStatus status)
    {
        using var f = new Fixture { Game = game };
        var r = await f.Client.GetSectionsAsync(974, new[] { IgdbMetadataSectionKind.Genres });
        Assert.Equal(status, r.Genres.Status); Assert.Equal(IgdbMetadataStatus.NotRequested, r.Themes.Status);
        Assert.Equal(IgdbMetadataStatus.NotRequested, r.TimeToBeat.Status); Assert.Single(f.Requests);
    }

    [Theory]
    [InlineData(401)] [InlineData(429)]
    public async Task SelectiveFailureKeepsExistingRetryPolicyAndDoesNotMarkUnrequestedSectionsFailed(int status)
    {
        using var f = new Fixture(); f.Override = (_, _) => Task.FromResult(Json("synthetic-sensitive", (HttpStatusCode)status));
        var r = await f.Client.GetSectionsAsync(974, new[] { IgdbMetadataSectionKind.Multiplayer });
        Assert.Equal(2, f.Requests.Count); Assert.Equal(IgdbMetadataStatus.TransportFailure, r.Multiplayer.Status);
        Assert.Equal(IgdbMetadataStatus.NotRequested, r.Genres.Status); Assert.Equal(IgdbMetadataStatus.NotRequested, r.TimeToBeat.Status);
    }

    [Fact]
    public async Task SelectiveCancellationAndInvalidSectionsAreRejectedBeforeHttp()
    {
        using var f = new Fixture(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Client.GetSectionsAsync(974, new[] { IgdbMetadataSectionKind.Genres }, cts.Token));
        await Assert.ThrowsAsync<IgdbQueryException>(() => f.Client.GetSectionsAsync(974, new[] { (IgdbMetadataSectionKind)99 }));
        await Assert.ThrowsAsync<IgdbQueryException>(() => f.Client.GetSectionsAsync(0, Array.Empty<IgdbMetadataSectionKind>()));
        await Assert.ThrowsAsync<IgdbQueryException>(() => f.Client.GetSectionsAsync(974, null!));
        Assert.Empty(f.Requests);
    }

}
