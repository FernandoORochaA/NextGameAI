using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;
using Microsoft.EntityFrameworkCore.Diagnostics;
using System.Data.Common;
using NextGameAI.Web.Controllers;

namespace NextGameAI.Web.Tests;

public sealed class IgdbLibraryTests
{
    private sealed class AccessFailureInterceptor : DbCommandInterceptor
    {
        public bool FailRead, FailWrite, Unexpected;
        public int Code = 5;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            if ((FailRead && command.CommandText.TrimStart().StartsWith("SELECT") && command.CommandText.Contains("FROM \"Games\"")) ||
                (FailWrite && command.CommandText.Contains("INSERT INTO \"LibraryEntries\"")))
            {
                if (Unexpected) throw new InvalidOperationException("synthetic unexpected failure");
                throw new SqliteException("synthetic database details must not appear", Code);
            }
            return base.ReaderExecutingAsync(command, eventData, result, ct);
        }
    }
    private sealed class Tokens : IIgdbTokenProvider
    {
        public bool IsConfigured => true;
        public Task<string> GetTokenAsync(CancellationToken cancellationToken = default) => Task.FromResult("synthetic");
        public void Invalidate(string token) { }
    }
    private sealed class Handler : HttpMessageHandler
    {
        public int Calls;
        public bool Unavailable;
        public Action? BeforeResponse;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            BeforeResponse?.Invoke();
            if (Unavailable) return new(HttpStatusCode.ServiceUnavailable);
            var query = await request.Content!.ReadAsStringAsync(ct);
            var match = Regex.Match(query, @"where id = (\d+);");
            var id = match.Success ? long.Parse(match.Groups[1].Value) : 974;
            var json = id is 974 or 132181
                ? System.Text.Json.JsonSerializer.Serialize(new[] { new
                {
                    id, name = "Resident Evil 4", cover = new { image_id = "co_test" },
                    first_release_date = id == 974 ? 1105401600 : 1679616000,
                    game_type = new { id = id == 974 ? 0 : 8, type = id == 974 ? "Main Game" : "Remake" },
                    version_title = "Standard"
                } })
                : "[]";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly LibraryApplication original = new();
        private readonly WebApplicationFactory<Program> app;
        public Handler HttpIgdb { get; } = new();
        public AccessFailureInterceptor AccessFailure { get; } = new();
        public HttpClient Http { get; }
        public IServiceProvider Services => app.Services;
        public Fixture()
        {
            app = original.WithWebHostBuilder(b => b.ConfigureServices(s =>
            {
                s.RemoveAll<IIgdbTokenProvider>(); s.AddSingleton<IIgdbTokenProvider>(new Tokens());
                s.Configure<NextGameAI.Web.Configuration.IgdbOptions>(o => { o.ClientId = "synthetic"; o.ClientSecret = "synthetic"; });
                s.AddHttpClient<IIgdbClient, IgdbClient>().ConfigurePrimaryHttpMessageHandler(() => HttpIgdb);
                s.AddDbContext<NextGameAiDbContext>(o => o.AddInterceptors(AccessFailure));
            }));
            Http = app.CreateClient(new() { BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false });
        }
        public async Task<Dictionary<string, string>> Form(string path = "/Library/Create?igdbId=974")
        {
            var html = await Http.GetStringAsync(path);
            return new()
            {
                ["__RequestVerificationToken"] = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value),
                ["IgdbId"] = "974", ["Status"] = "1", ["Rating"] = "7", ["Notes"] = "Minhas notas",
                ["SelectedPlatforms[0]"] = "pc", ["SelectedPlatforms[1]"] = "playstation"
            };
        }
        public Task<HttpResponseMessage> Save(Dictionary<string, string> form) =>
            Http.PostAsync("/Library/Create", new FormUrlEncodedContent(form));
        public void Dispose() { Http.Dispose(); app.Dispose(); original.Dispose(); }
    }

    [Fact]
    public async Task SelectionOpensCorrectFormAndDoesNotPersist()
    {
        using var f = new Fixture();
        var html = WebUtility.HtmlDecode(await f.Http.GetStringAsync("/Library/Create?igdbId=132181"));
        Assert.Contains("Resident Evil 4", html); Assert.Contains("132181", html);
        Assert.Contains("2023", html); Assert.Contains("Remake", html);
        Assert.Contains("co_test.jpg", html); Assert.Contains("SelectedPlatforms", html);
        Assert.DoesNotContain("name=\"Title\"", html);
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().Games.ToListAsync());
    }

    [Theory]
    [InlineData(5)] [InlineData(6)] [InlineData(8)] [InlineData(10)] [InlineData(13)] [InlineData(14)]
    public async Task ExpectedSqliteReadFailureShowsFriendlyGetAndPreservesPostWithoutWrites(int code)
    {
        using var f = new Fixture();
        var form = await f.Form("/Library/Create");
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var oldId = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
        { Title = "Jogo preservado", Rating = 0, Notes = "Não alterar", SelectedPlatforms = ["xbox"] });
        f.AccessFailure.Code = code; f.AccessFailure.FailRead = true;
        var get = await f.Http.GetAsync("/Library/Create?igdbId=974");
        Assert.Equal(HttpStatusCode.OK, get.StatusCode);
        var getHtml = WebUtility.HtmlDecode(await get.Content.ReadAsStringAsync());
        Assert.Contains("Não foi possível acessar a biblioteca agora.", getHtml);
        Assert.DoesNotContain("synthetic database", getHtml);
        var post = await f.Save(form);
        Assert.Equal(HttpStatusCode.OK, post.StatusCode);
        var html = WebUtility.HtmlDecode(await post.Content.ReadAsStringAsync());
        Assert.Contains("Seus dados foram mantidos", html);
        Assert.Contains("Minhas notas", html);
        Assert.Contains("value=\"7\" checked=\"checked\"", html);
        Assert.Contains("value=\"pc\"", html); Assert.Contains("name=\"IgdbId\"", html);
        Assert.DoesNotContain("synthetic database", html);
        Assert.DoesNotContain("SqliteException", html);
        f.AccessFailure.FailRead = false;
        db.ChangeTracker.Clear();
        Assert.Single(await db.Games.ToListAsync());
        var entry = await db.LibraryEntries.SingleAsync();
        Assert.Equal(oldId, entry.Id); Assert.Equal(0, entry.Rating); Assert.Equal("Não alterar", entry.Notes);
        Assert.Single(await db.GameOwnerships.ToListAsync());
        Assert.Equal(0, f.HttpIgdb.Calls);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExpectedSqliteWriteFailureUsesSameMessageAndRollsBack(bool external)
    {
        using var f = new Fixture(); var form = await f.Form("/Library/Create");
        if (!external) { form.Remove("IgdbId"); form["Title"] = "Manual"; }
        f.AccessFailure.FailWrite = true;
        var response = await f.Save(form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Não foi possível acessar a biblioteca agora.", html);
        Assert.Contains("Minhas notas", html); Assert.DoesNotContain("synthetic database", html);
        f.AccessFailure.FailWrite = false;
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Empty(await db.Games.ToListAsync()); Assert.Empty(await db.LibraryEntries.ToListAsync());
        Assert.Empty(await db.GameOwnerships.ToListAsync());
        Assert.False(await db.Stores.AnyAsync(s => s.Code == "unspecified"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnexpectedPreparationErrorsAreNotHidden(bool programmingError)
    {
        using var f = new Fixture();
        using var scope = f.Services.CreateScope();
        var controller = new LibraryController(scope.ServiceProvider.GetRequiredService<LibraryService>());
        f.AccessFailure.FailRead = true;
        f.AccessFailure.Code = 1;
        f.AccessFailure.Unexpected = programmingError;
        if (programmingError)
            await Assert.ThrowsAsync<InvalidOperationException>(() => controller.Create(974, CancellationToken.None));
        else
            await Assert.ThrowsAsync<SqliteException>(() => controller.Create(974, CancellationToken.None));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task UnexpectedWriteErrorsAreNotPresentedAsExpectedValidationFailures(bool programmingError)
    {
        using var f = new Fixture(); var form = await f.Form("/Library/Create");
        f.AccessFailure.FailWrite = true; f.AccessFailure.Code = 1;
        f.AccessFailure.Unexpected = programmingError;
        var response = await f.Save(form);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.DoesNotContain("Seus dados foram mantidos", WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync()));
        f.AccessFailure.FailWrite = false;
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Empty(await db.Games.ToListAsync()); Assert.Empty(await db.LibraryEntries.ToListAsync());
    }

    [Fact]
    public async Task SavesTrustedMetadataAndPersonalDataWithMultiplePlatforms()
    {
        using var f = new Fixture();
        var form = await f.Form();
        form["Title"] = "Forged"; form["EditionTitle"] = "Forged"; form["CoverImageId"] = "evil";
        var response = await f.Save(form);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var game = await db.Games.SingleAsync();
        var entry = await db.LibraryEntries.Include(e => e.Ownerships).SingleAsync();
        Assert.Equal(974, game.IgdbId); Assert.Equal("Resident Evil 4", game.Title);
        Assert.Equal("Standard", game.EditionTitle); Assert.Equal("co_test", game.CoverImageId);
        Assert.Equal(2005, game.FirstReleaseDate!.Value.Year); Assert.Equal(0, game.GameTypeId);
        Assert.Equal(game.Id, entry.GameId); Assert.Equal(7, entry.Rating);
        Assert.Equal(LibraryStatus.Playing, entry.Status); Assert.Equal("Minhas notas", entry.Notes);
        Assert.Equal(2, entry.Ownerships.Count);
        var details = WebUtility.HtmlDecode(await f.Http.GetStringAsync(response.Headers.Location));
        Assert.Contains("co_test.jpg", details); Assert.Contains("2005", details); Assert.Contains("Jogo principal", details);
        Assert.Contains("co_test.jpg", await f.Http.GetStringAsync("/Library"));
    }

    [Theory]
    [InlineData("")] [InlineData("0")] [InlineData("1")] [InlineData("10")]
    public async Task RatingKeepsUnratedZeroAndHalfStars(string rating)
    {
        using var f = new Fixture(); var form = await f.Form(); form["Rating"] = rating;
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        using var scope = f.Services.CreateScope();
        Assert.Equal(rating == "" ? (int?)null : int.Parse(rating),
            (await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().LibraryEntries.SingleAsync()).Rating);
    }

    [Fact]
    public async Task LocalIdentityIsReusedWithoutExternalCallsAndSurvivesDeletion()
    {
        using var f = new Fixture();
        using (var scope = f.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            db.Games.Add(new Game { Id = 77, IgdbId = 974, Title = "Título local", GameTypeId = 0 });
            await db.SaveChangesAsync();
        }
        f.HttpIgdb.Unavailable = true;
        var form = await f.Form();
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        using var check = f.Services.CreateScope();
        var library = check.ServiceProvider.GetRequiredService<LibraryService>();
        var dbCheck = check.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var entry = await dbCheck.LibraryEntries.SingleAsync(); Assert.Equal(77, entry.GameId);
        Assert.True(await library.DeleteAsync(entry.Id));
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        Assert.Equal(1, await dbCheck.Games.CountAsync()); Assert.Equal(0, f.HttpIgdb.Calls);
    }

    [Fact]
    public async Task DuplicateOffersDetailsAndEditWithoutMutatingExistingEntry()
    {
        using var f = new Fixture(); var form = await f.Form();
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        form["Notes"] = "Não substituir";
        var response = await f.Save(form);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("já está na sua biblioteca", html);
        Assert.Contains("Editar jogo cadastrado", html); Assert.Contains("Ver detalhes", html);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Single(await db.LibraryEntries.ToListAsync()); Assert.Single(await db.Games.ToListAsync());
        Assert.Equal("Minhas notas", (await db.LibraryEntries.SingleAsync()).Notes);
    }

    [Fact]
    public async Task OriginalRemakeAndOldManualTitleRemainIndependent()
    {
        using var f = new Fixture();
        var form = await f.Form();
        form.Remove("IgdbId"); form["Title"] = "Resident Evil 4";
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        form["IgdbId"] = "974";
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        form["IgdbId"] = "132181";
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Equal(3, await db.Games.CountAsync()); Assert.Equal(3, await db.LibraryEntries.CountAsync());
        Assert.Equal(1, await db.Games.CountAsync(g => g.IgdbId == null));
        Assert.Equal(1, await db.Games.CountAsync(g => g.IgdbId == 974));
        Assert.Equal(1, await db.Games.CountAsync(g => g.IgdbId == 132181));
    }

    [Theory]
    [InlineData("0")] [InlineData("-1")] [InlineData("abc")] [InlineData("")] [InlineData("999")]
    public async Task InvalidOrAbsentExternalIdentityNeverBecomesManual(string id)
    {
        using var f = new Fixture(); var form = await f.Form("/Library/Create");
        form["IgdbId"] = id; form["Title"] = "Do not save";
        var response = await f.Save(form);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("name=\"IgdbId\"", await response.Content.ReadAsStringAsync());
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Empty(await db.Games.ToListAsync()); Assert.Empty(await db.LibraryEntries.ToListAsync());
    }

    [Fact]
    public async Task OutagePreservesInputAndManualRegistrationStillWorks()
    {
        using var f = new Fixture(); var form = await f.Form("/Library/Create");
        f.HttpIgdb.Unavailable = true;
        var response = await f.Save(form);
        var html = WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());
        Assert.Contains("Seus dados foram mantidos", html); Assert.Contains("Minhas notas", html);
        Assert.Contains("value=\"pc\"", html);
        form.Remove("IgdbId"); form["Title"] = "Manual offline";
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        using var scope = f.Services.CreateScope();
        Assert.Null((await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().Games.SingleAsync()).IgdbId);
    }

    [Fact]
    public async Task MissingPlatformsAndAntiforgeryCannotPersist()
    {
        using var f = new Fixture(); var form = await f.Form();
        form.Remove("__RequestVerificationToken");
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Save(form)).StatusCode);
        form = await f.Form(); form.Remove("SelectedPlatforms[0]"); form.Remove("SelectedPlatforms[1]");
        Assert.Equal(HttpStatusCode.OK, (await f.Save(form)).StatusCode);
        using var scope = f.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().Games.ToListAsync());
    }

    [Fact]
    public async Task FailedEntryInsertRollsBackCatalogAndNewReferences()
    {
        using var f = new Fixture(); var form = await f.Form();
        using var scope = f.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailEntry BEFORE INSERT ON LibraryEntries BEGIN SELECT RAISE(ABORT, 'synthetic failure'); END;");
        Assert.Equal(HttpStatusCode.OK, (await f.Save(form)).StatusCode);
        Assert.Empty(await db.Games.AsNoTracking().ToListAsync()); Assert.Empty(await db.LibraryEntries.ToListAsync());
        Assert.Empty(await db.GameOwnerships.ToListAsync());
        Assert.False(await db.Stores.AnyAsync(s => s.Code == "unspecified"));
        Assert.False(await db.Platforms.AnyAsync(p => p.Code == "playstation"));
    }

    [Fact]
    public async Task IdentityInsertedWhileIgdbRespondsIsRecheckedInsideTransaction()
    {
        using var f = new Fixture(); var form = await f.Form("/Library/Create");
        f.HttpIgdb.BeforeResponse = () =>
        {
            using var scope = f.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            db.Games.Add(new Game { Id = 55, IgdbId = 974, Title = "Catálogo concorrente" });
            db.SaveChanges();
            f.HttpIgdb.BeforeResponse = null;
        };
        Assert.Equal(HttpStatusCode.Redirect, (await f.Save(form)).StatusCode);
        using var check = f.Services.CreateScope();
        var context = check.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Equal(55, (await context.LibraryEntries.SingleAsync()).GameId);
        Assert.Single(await context.Games.ToListAsync());
    }

    [Fact]
    public async Task AdditiveMigrationPreservesOldSqliteRowsAndRelations()
    {
        using var connection = new SqliteConnection("Data Source=:memory:;Foreign Keys=True"); connection.Open();
        using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261022004238_InitialLibrary");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO Games(Id,Title,EditionTitle,IgdbId) VALUES(70,'Manual antigo','Edição preservada',NULL);");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO LibraryEntries(Id,GameId,UserProfileId,Status,Rating,Notes,AddedAtUtc,UpdatedAtUtc) VALUES(80,70,1,1,0,'Manter','2026-01-01','2026-01-01');");
        await db.Database.ExecuteSqlRawAsync("INSERT INTO GameOwnerships(Id,LibraryEntryId,StoreId,PlatformId) VALUES(90,80,1,1);");
        await migrator.MigrateAsync();
        var game = await db.Games.SingleAsync(); Assert.Equal(70, game.Id); Assert.Null(game.IgdbId);
        Assert.Null(game.CoverImageId); Assert.Null(game.FirstReleaseDate); Assert.Null(game.GameTypeId); Assert.Null(game.GameType);
        Assert.Equal("Edição preservada", game.EditionTitle);
        var entry = await db.LibraryEntries.SingleAsync(); Assert.Equal(80, entry.Id); Assert.Equal(70, entry.GameId);
        Assert.Equal(0, entry.Rating); Assert.Equal("Manter", entry.Notes);
        var ownership = await db.GameOwnerships.SingleAsync(); Assert.Equal(90, ownership.Id); Assert.Equal(80, ownership.LibraryEntryId);
        var sql = migrator.GenerateScript("20261022004238_InitialLibrary");
        Assert.Contains("ADD", sql); Assert.DoesNotContain("DROP TABLE", sql);
        Assert.DoesNotContain("CREATE TABLE \"ef_temp", sql);
    }

    [Fact]
    public async Task EditingAndDeletingIgdbEntryPreserveCatalogAndOtherGames()
    {
        using var f = new Fixture();
        using var scope = f.Services.CreateScope();
        var library = scope.ServiceProvider.GetRequiredService<LibraryService>();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var manualId = await library.SaveSimpleAsync(new SimpleLibraryForm
        { Title = "Manual antigo", Rating = 0, Notes = "Preservar", SelectedPlatforms = ["xbox"] });
        var response = await f.Save(await f.Form());
        var path = response.Headers.Location!.ToString();
        var id = int.Parse(path.Split('/').Last());
        var html = await f.Http.GetStringAsync($"/Library/Edit/{id}");
        var token = WebUtility.HtmlDecode(Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);
        var edit = new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token, ["Title"] = "Não mudar o catálogo", ["Status"] = "2",
            ["Rating"] = "1", ["Notes"] = "Edição pessoal", ["SelectedPlatforms[0]"] = "pc",
            ["ConfirmedPlatformRemovals[0]"] = "playstation"
        };
        Assert.Equal(HttpStatusCode.Redirect, (await f.Http.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(edit))).StatusCode);
        db.ChangeTracker.Clear();
        var igdbEntry = await db.LibraryEntries.Include(e => e.Game).Include(e => e.Ownerships).SingleAsync(e => e.Id == id);
        Assert.Equal("Resident Evil 4", igdbEntry.Game.Title);
        Assert.Equal(974, igdbEntry.Game.IgdbId); Assert.Equal("co_test", igdbEntry.Game.CoverImageId);
        Assert.Equal(1, igdbEntry.Rating); Assert.Single(igdbEntry.Ownerships);
        Assert.Equal(LibraryStatus.Completed, igdbEntry.Status);
        var old = await db.LibraryEntries.Include(e => e.Game).Include(e => e.Ownerships).SingleAsync(e => e.Id == manualId);
        Assert.Equal("Manual antigo", old.Game.Title); Assert.Null(old.Game.IgdbId);
        Assert.Equal(0, old.Rating); Assert.Equal("Preservar", old.Notes); Assert.Single(old.Ownerships);
        Assert.True(await library.DeleteAsync(id));
        Assert.Equal(2, await db.Games.CountAsync());
        Assert.Equal(manualId, (await db.LibraryEntries.SingleAsync()).Id);
    }
}
