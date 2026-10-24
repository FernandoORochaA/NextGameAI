using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NextGameAI.Web.Controllers;
using NextGameAI.Web.Data;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;
using Microsoft.EntityFrameworkCore;

namespace NextGameAI.Web.Tests;

public sealed class CatalogTests
{
    private sealed class FakeIgdb : IIgdbClient
    {
        public int Calls;
        public string? Term;
        public CancellationToken Token;
        public Exception? Error;
        public IReadOnlyList<IgdbGameResult> Games = [];
        public Task<IReadOnlyList<IgdbGameResult>> SearchAsync(string term, CancellationToken cancellationToken = default)
        {
            Calls++; Term = term; Token = cancellationToken;
            if (Error is not null) throw Error;
            return Task.FromResult(Games);
        }
        public Task<IgdbGameResult?> GetByIdAsync(long id, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The search page must not query details.");
    }

    private static IgdbGameResult Game(long id = 974, string title = "Resident Evil 4") =>
        new(id, title, "co_test", new DateOnly(2005, 1, 11), 0, "Main Game", "Gold", null);

    private sealed class Fixture : IDisposable
    {
        public FakeIgdb Igdb { get; } = new();
        private readonly LibraryApplication original = new();
        private readonly Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<Program> app;
        public HttpClient Http { get; }
        public IServiceProvider Services => app.Services;
        public Fixture()
        {
            app = original.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IIgdbClient>();
                s.AddSingleton<IIgdbClient>(Igdb);
            }));
            Http = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        }
        public void Dispose() { Http.Dispose(); app.Dispose(); original.Dispose(); }
    }

    [Fact]
    public async Task PageAndMenuAreAccessibleWithoutGamesAndDoNotSearch()
    {
        using var f = new Fixture();
        var html = WebUtility.HtmlDecode(await f.Http.GetStringAsync("/Catalog"));
        Assert.Contains("Pesquisar jogos", html);
        Assert.Contains("for=\"catalog-term\"", html);
        Assert.Contains("role=\"status\"", html);
        Assert.Contains("catalog-search.js", html);
        Assert.Contains("Pesquisar jogos na IGDB", html);
        Assert.Contains("Digite o nome de um jogo para começar.", html);
        Assert.Equal(0, f.Igdb.Calls);
    }

    [Fact]
    public async Task LibraryAddButtonLeadsToSearch()
    {
        using var f = new Fixture();
        var html = WebUtility.HtmlDecode(await f.Http.GetStringAsync("/Library"));
        var button = System.Text.RegularExpressions.Regex.Match(html, "<a[^>]*class=\"btn btn-primary\"[^>]*>Adicionar jogo</a>");
        Assert.True(button.Success);
        Assert.Contains("href=\"/Catalog\"", button.Value);
        Assert.DoesNotContain("href=\"/Library/Create\"", button.Value);
        Assert.Equal(0, f.Igdb.Calls);
    }

    [Fact]
    public async Task ManualAlternativeRemainsAvailableAfterExternalFailure()
    {
        using var f = new Fixture();
        f.Igdb.Error = new IgdbQueryException(IgdbQueryFailure.Unavailable);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Http.GetAsync("/Catalog/Search?term=Resident")).StatusCode);
        var html = WebUtility.HtmlDecode(await f.Http.GetStringAsync("/Catalog"));
        Assert.Contains("Não encontrou o jogo?", html);
        Assert.Contains("href=\"/Library/Create\">Cadastrar manualmente</a>", html);
        var manual = await f.Http.GetStringAsync("/Library/Create");
        Assert.Contains("name=\"Title\"", manual);
        Assert.DoesNotContain("name=\"IgdbId\"", manual);
        Assert.Equal(1, f.Igdb.Calls);
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData(" ")] [InlineData("ab")]
    [InlineData("line\nbreak")] [InlineData("abc\u200bxyz")]
    public async Task InvalidTermsAreRejectedBeforeService(string? term)
    {
        using var f = new Fixture();
        var response = await f.Http.GetAsync("/Catalog/Search?term=" + Uri.EscapeDataString(term ?? ""));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("invalidInput", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(0, f.Igdb.Calls);
    }

    [Fact]
    public async Task ExcessivelyLongTermIsRejectedBeforeService()
    {
        using var f = new Fixture();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Http.GetAsync("/Catalog/Search?term=" + new string('a', 101))).StatusCode);
        Assert.Equal(0, f.Igdb.Calls);
    }

    [Theory]
    [InlineData("Halo")] [InlineData("Resident Evil 4")] [InlineData("Ação e aventura")]
    public async Task ValidTermsReachService(string term)
    {
        using var f = new Fixture();
        Assert.Equal(HttpStatusCode.OK, (await f.Http.GetAsync("/Catalog/Search?term=" + Uri.EscapeDataString(term))).StatusCode);
        Assert.Equal(term, f.Igdb.Term);
        Assert.Equal(1, f.Igdb.Calls);
    }

    [Fact]
    public async Task CompleteResultsUseSafeCoversAndKeepIdenticalTitlesIndependent()
    {
        using var f = new Fixture();
        f.Igdb.Games = [Game(), Game(132181) with { GameTypeId = 8, GameType = "Remake", FirstReleaseDate = new DateOnly(2023, 3, 24) }];
        var response = await f.Http.GetAsync("/Catalog/Search?term=Resident");
        var games = (await response.Content.ReadFromJsonAsync<CatalogGameResult[]>())!;
        Assert.Equal(new long[] { 974, 132181 }, games.Select(g => g.IgdbId));
        Assert.Equal(2005, games[0].ReleaseYear);
        Assert.Equal("Jogo principal", games[0].GameType);
        Assert.Equal("Remake", games[1].GameType);
        Assert.Equal("Gold", games[0].EditionTitle);
        Assert.Equal("https://images.igdb.com/igdb/image/upload/t_cover_big/co_test.jpg", games[0].CoverUrl);
        Assert.All(games, g => Assert.Equal("Resident Evil 4", g.Title));
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }

    [Fact]
    public async Task OptionalMetadataCanBeMissingAndArbitraryImageUrlsAreRejected()
    {
        using var f = new Fixture();
        f.Igdb.Games = [Game() with { CoverImageId = null, FirstReleaseDate = null, GameTypeId = null, GameType = null, EditionTitle = null },
            Game(2) with { CoverImageId = "https://evil.example/image" }];
        var games = (await f.Http.GetFromJsonAsync<CatalogGameResult[]>("/Catalog/Search?term=Resident"))!;
        Assert.Null(games[0].CoverUrl); Assert.Null(games[0].ReleaseYear); Assert.Null(games[0].GameType);
        Assert.Null(games[0].EditionTitle); Assert.Null(games[1].CoverUrl);
    }

    [Fact]
    public async Task EmptyResultIsSuccessfulAndMaximumIsTen()
    {
        using var f = new Fixture();
        Assert.Empty((await f.Http.GetFromJsonAsync<CatalogGameResult[]>("/Catalog/Search?term=Unknown"))!);
        f.Igdb.Games = Enumerable.Range(1, 12).Select(i => Game(i)).ToArray();
        Assert.Equal(10, (await f.Http.GetFromJsonAsync<CatalogGameResult[]>("/Catalog/Search?term=Resident"))!.Length);
    }

    [Theory]
    [InlineData(IgdbQueryFailure.Unavailable, 503, "unavailable")]
    [InlineData(IgdbQueryFailure.Timeout, 503, "unavailable")]
    [InlineData(IgdbQueryFailure.InvalidResponse, 503, "unavailable")]
    [InlineData(IgdbQueryFailure.NotConfigured, 503, "unavailable")]
    [InlineData(IgdbQueryFailure.AuthenticationUnavailable, 503, "authentication")]
    [InlineData(IgdbQueryFailure.Unauthorized, 503, "authentication")]
    [InlineData(IgdbQueryFailure.RateLimited, 429, "rateLimited")]
    [InlineData(IgdbQueryFailure.InvalidInput, 400, "invalidInput")]
    public async Task ExternalFailuresHaveDistinctSafeResponses(IgdbQueryFailure failure, int status, string code)
    {
        using var f = new Fixture();
        f.Igdb.Error = new IgdbQueryException(failure);
        var response = await f.Http.GetAsync("/Catalog/Search?term=Resident");
        Assert.Equal(status, (int)response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, json.GetProperty("code").GetString());
        Assert.False(json.TryGetProperty("stackTrace", out _));
    }

    [Fact]
    public async Task UnexpectedErrorsNeverExposeSensitiveMessages()
    {
        using var f = new Fixture();
        f.Igdb.Error = new Exception("SYNTHETIC_SECRET SYNTHETIC_TOKEN Authorization Bearer synthetic");
        var response = await f.Http.GetAsync("/Catalog/Search?term=Resident");
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("SYNTHETIC", body);
        Assert.DoesNotContain("Authorization", body);
        Assert.Contains("unexpected", body);
    }

    [Fact]
    public async Task JsonContainsOnlyPublicFieldsAndEncodesUntrustedText()
    {
        using var f = new Fixture();
        f.Igdb.Games = [Game(title: "<script>alert('x')</script>")];
        var response = await f.Http.GetAsync("/Catalog/Search?term=Resident");
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("<script>", body);
        using var json = JsonDocument.Parse(body);
        Assert.Equal(new[] { "coverUrl", "editionTitle", "gameType", "igdbId", "releaseYear", "title" },
            json.RootElement[0].EnumerateObject().Select(p => p.Name).OrderBy(n => n).ToArray());
        Assert.Equal("<script>alert('x')</script>", json.RootElement[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task LocalEndpointHasBoundedRateLimit()
    {
        using var f = new Fixture();
        for (var i = 0; i < 30; i++)
            Assert.Equal(HttpStatusCode.OK, (await f.Http.GetAsync("/Catalog/Search?term=Resident")).StatusCode);
        var response = await f.Http.GetAsync("/Catalog/Search?term=Resident");
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal("rateLimited", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        Assert.Equal(30, f.Igdb.Calls);
    }

    [Fact]
    public async Task CancellationTokenReachesService()
    {
        var fake = new FakeIgdb();
        using var cts = new CancellationTokenSource();
        await new CatalogController(fake).Search("Resident", cts.Token);
        Assert.Equal(cts.Token, fake.Token);
        cts.Cancel();
        fake.Error = new OperationCanceledException(cts.Token);
        Assert.Equal(499, Assert.IsType<StatusCodeResult>(await new CatalogController(fake).Search("Resident", cts.Token)).StatusCode);
    }

    [Fact]
    public async Task SearchPreservesExistingLibrary()
    {
        using var f = new Fixture();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var id = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
        { Title = "Preservar", Rating = 0, Notes = "Anotação pessoal", SelectedPlatforms = ["pc", "xbox"] });
        var before = await db.LibraryEntries.AsNoTracking().SingleAsync();
        f.Igdb.Games = [Game()];
        await f.Http.GetStringAsync("/Catalog/Search?term=Resident");
        db.ChangeTracker.Clear();
        var after = await db.LibraryEntries.AsNoTracking().SingleAsync();
        Assert.Equal(id, after.Id);
        Assert.Equal(before.Rating, after.Rating); Assert.Equal(before.Status, after.Status);
        Assert.Equal(before.Notes, after.Notes);
        Assert.Single(await db.Games.ToListAsync());
        Assert.Equal(2, await db.GameOwnerships.CountAsync());
    }
}
