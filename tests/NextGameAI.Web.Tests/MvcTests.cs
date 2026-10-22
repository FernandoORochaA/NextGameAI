using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Tests;

public sealed class LibraryApplication : WebApplicationFactory<Program>
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");

    public LibraryApplication() => connection.Open();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<NextGameAiDbContext>>();
            services.RemoveAll<IDbContextOptionsConfiguration<NextGameAiDbContext>>();
            services.AddDbContext<NextGameAiDbContext>(options => options.UseSqlite(connection));
            using var db = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).Options);
            db.Database.Migrate();
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing) connection.Dispose();
    }
}

public class MvcTests
{
    private static HttpClient Client(LibraryApplication app) => app.CreateClient(new()
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost")
    });

    private static async Task<Dictionary<string, string>> Form(HttpClient client, string path)
    {
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "O formulário deve fornecer token antiforgery.");
        return new() { ["__RequestVerificationToken"] = WebUtility.HtmlDecode(token.Groups[1].Value) };
    }

    private static void SetValid(Dictionary<string, string> form)
    {
        form["Title"] = "Jogo MVC";
        form["Status"] = "1";
        form["Rating"] = "7";
        form["SelectedPlatforms[0]"] = "pc";
    }

    private static string Decode(string text) => WebUtility.HtmlDecode(text);

    [Fact]
    public async Task BrowserFormsCreateReadEditAndDeleteWithAntiforgery()
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        var empty = Decode(await client.GetStringAsync("/Library"));
        Assert.Contains("Sua biblioteca está vazia", empty);
        var form = await Form(client, "/Library/Create"); SetValid(form);
        form["SelectedPlatforms[1]"] = "playstation";
        var response = await client.PostAsync("/Library/Create", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location!.ToString();
        var id = location.Split('/').Last();
        var details = Decode(await client.GetStringAsync(location));
        Assert.Contains("Jogo MVC", details);
        Assert.Contains("3,5 / 5 estrelas", details);
        Assert.DoesNotContain("Loja não informada", details); Assert.Contains("PlayStation", details);
        var editHtml = await client.GetStringAsync($"/Library/Edit/{id}");
        Assert.DoesNotContain("name=\"ExistingGameId\"", editHtml);
        Assert.DoesNotContain("name=\"EditionTitle\"", editHtml);
        Assert.DoesNotContain("type=\"hidden\" name=\"SelectedPlatforms\"", editHtml);
        var edit = await Form(client, $"/Library/Edit/{id}");
        SetValid(edit);
        edit["SelectedPlatforms[1]"] = "playstation";
        edit["Status"] = "3"; edit["Rating"] = "0"; edit["AbandonmentReason"] = "Não gostei";
        response = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        details = Decode(await client.GetStringAsync(location));
        Assert.Contains("0 / 5 estrelas", details); Assert.Contains("Não gostei", details);
        edit["Status"] = "2";
        response = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(edit));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.DoesNotContain("Não gostei", Decode(await client.GetStringAsync(location)));
        var delete = await Form(client, $"/Library/Delete/{id}");
        response = await client.PostAsync($"/Library/Delete/{id}", new FormUrlEncodedContent(delete));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("Sua biblioteca está vazia", Decode(await client.GetStringAsync("/Library")));
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Single(await db.Games.ToListAsync());
        Assert.Empty(await db.GameOwnerships.ToListAsync());
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("rating")]
    [InlineData("status")] [InlineData("text")] [InlineData("malformed")]
    public async Task InvalidFormsRedisplayErrorsAndPreserveInput(string error)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        var form = await Form(client, "/Library/Create"); SetValid(form);
        switch (error)
        {
            case "missing": form.Remove("SelectedPlatforms[0]"); break;
            case "duplicate": form["SelectedPlatforms[1]"] = "pc"; break;
            case "rating": form["Rating"] = "11"; break;
            case "status": form["Status"] = "99"; break;
            case "text": form["Notes"] = new('x', 5001); break;
            case "malformed": form["Rating"] = "0.5"; break;
        }
        var response = await client.PostAsync("/Library/Create", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("validation-summary-errors", html);
        Assert.Contains("Jogo MVC", html);
        using var scope = app.Services.CreateScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().Games.ToListAsync());
    }

    [Theory]
    [InlineData("/Library/Create")] [InlineData("/Library/Edit/1")] [InlineData("/Library/Delete/1")]
    public async Task MutationsWithoutAntiforgeryAreRejected(string path)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync(path, new FormUrlEncodedContent([]))).StatusCode);
    }

    [Theory]
    [InlineData("/Library/Details/999")] [InlineData("/Library/Edit/999")] [InlineData("/Library/Delete/999")]
    public async Task UnknownEntryReturnsNotFound(string path)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task MainFormHasFiveStarsAndNoTechnicalCatalogOrOwnershipFields()
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        var html = await client.GetStringAsync("/Library/Create");
        Assert.Equal(5, Regex.Matches(html, "data-star=\"[1-5]\"").Count);
        Assert.Equal(12, Regex.Matches(html, "type=\"radio\" name=\"Rating\"").Count);
        Assert.DoesNotContain("name=\"EditionTitle\"", html);
        Assert.DoesNotContain("name=\"ExistingGameId\"", html);
        Assert.DoesNotContain("Ownerships[", html);
        Assert.DoesNotContain("<select name=\"Rating\"", html);
        Assert.Equal(5, Regex.Matches(html, "type=\"checkbox\" name=\"SelectedPlatforms\"").Count);
    }

    [Fact]
    public async Task LegacyEditGroupsPlatformsAndIgnoresPostedTechnicalFields()
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        int id;
        List<(int Id, int StoreId, int PlatformId)> original;
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            var game = new Game { Title = "Legado", EditionTitle = "Deluxe", IgdbId = 321 };
            db.Games.Add(game); await db.SaveChangesAsync();
            id = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveAsync(new LibraryForm
            {
                ExistingGameId = game.Id,
                Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 2, PlatformId = 1 },
                    new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 4, PlatformId = 3 }]
            });
            original = (await db.GameOwnerships.ToListAsync()).Select(x => (x.Id, x.StoreId, x.PlatformId)).ToList();
        }
        var html = await client.GetStringAsync($"/Library/Edit/{id}");
        foreach (var group in new[] { "pc", "playstation" })
        {
            var checkbox = Regex.Match(html, $"<input type=\"checkbox\"[^>]*id=\"platform-{group}\"[^>]*>").Value;
            Assert.Contains("checked=\"checked\"", checkbox);
            Assert.DoesNotContain("disabled", checkbox);
            Assert.DoesNotContain($"type=\"hidden\" name=\"SelectedPlatforms\" value=\"{group}\"", html);
        }
        var form = await Form(client, $"/Library/Edit/{id}"); SetValid(form);
        form["SelectedPlatforms[0]"] = "pc";
        form["SelectedPlatforms[1]"] = "playstation";
        form["SelectedPlatforms[2]"] = "xbox";
        form["EditionTitle"] = "Não aplicar";
        form["ExistingGameId"] = "999";
        form["Ownerships[0].StoreId"] = "999";
        form["Ownerships[0].PlatformId"] = "999";
        var response = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        using var verifyScope = app.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var entry = await verify.LibraryEntries.Include(x => x.Game).Include(x => x.Ownerships).SingleAsync();
        Assert.Equal("Deluxe", entry.Game.EditionTitle);
        Assert.Equal(321, entry.Game.IgdbId);
        Assert.Equal("Legado", entry.Game.Title);
        Assert.Equal(5, entry.Ownerships.Count);
        foreach (var old in original)
            Assert.Contains(entry.Ownerships, x => x.Id == old.Id && x.StoreId == old.StoreId && x.PlatformId == old.PlatformId);
    }

    [Theory]
    [InlineData("/Library")]
    [InlineData("/Library/Details/1")]
    public async Task LibraryAndDetailsShowDistinctGroupsWithoutStoresAndPreserveLegacyRows(string path)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            var unknown = new Platform { Code = "custom-platform", Name = "Console antigo" };
            var generic = new Platform { Code = "playstation", Name = "PlayStation" };
            db.Platforms.AddRange(unknown, generic); await db.SaveChangesAsync();
            await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveAsync(new LibraryForm
            {
                Title = "Jogo agrupado", EditionTitle = "Deluxe", Rating = 7,
                Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 2, PlatformId = 1 },
                    new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 4, PlatformId = 3 },
                    new() { StoreId = 4, PlatformId = generic.Id }, new() { StoreId = 5, PlatformId = 4 },
                    new() { StoreId = 5, PlatformId = 5 }, new() { StoreId = 6, PlatformId = 6 },
                    new() { StoreId = 7, PlatformId = unknown.Id }]
            });
        }
        var html = Decode(await client.GetStringAsync(path));
        foreach (var name in new[] { "PC", "PlayStation", "Nintendo", "Xbox", "Outros" })
            Assert.Single(Regex.Matches(html, $"<li>\\s*{name}\\s*</li>"));
        foreach (var hidden in new[] { "Steam", "GOG", "PlayStation Store", "Microsoft Store", "Nintendo eShop",
            "Mídia física", "Loja não informada", "PlayStation 4", "PlayStation 5", "Xbox One", "Xbox Series X/S", "Nintendo Switch", "Console antigo" })
            Assert.DoesNotContain(hidden, html);
        Assert.DoesNotContain("Lojas e plataformas", html);
        Assert.DoesNotContain("Jogo / edição", html);
        Assert.Contains("Deluxe", html);
        Assert.Contains("3,5 / 5 estrelas", html);
        Assert.Contains("Editar", html);
        Assert.Contains("Excluir", html);
        if (path == "/Library") Assert.Contains("<th>Jogo</th>", html);
        using var verifyScope = app.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Equal(9, await verify.GameOwnerships.CountAsync());
        Assert.True(await verify.GameOwnerships.AnyAsync(x => x.Store.Code == "steam" && x.Platform.Code == "pc"));
        Assert.True(await verify.GameOwnerships.AnyAsync(x => x.Store.Code == "playstation-store" && x.Platform.Code == "ps5"));
    }

    [Theory]
    [InlineData("pc", "PC")]
    [InlineData("playstation", "PlayStation")]
    [InlineData("nintendo", "Nintendo")]
    [InlineData("xbox", "Xbox")]
    [InlineData("other", "Outros")]
    public async Task GenericPlatformDisplaysOnlyItsGroup(string code, string name)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        using (var scope = app.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
            { Title = "Jogo genérico", SelectedPlatforms = [code] });
        var html = Decode(await client.GetStringAsync("/Library"));
        Assert.Single(Regex.Matches(html, "<li>"));
        Assert.Matches($"<li>\\s*{name}\\s*</li>", html);
        Assert.DoesNotContain("Loja não informada", html);
    }

    [Fact]
    public async Task RemovalRequiresExplicitConfirmationAndRetainsSubmittedValuesOnError()
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        int id;
        int pcOwnershipId;
        using (var scope = app.Services.CreateScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<LibraryService>();
            id = await service.SaveAsync(new LibraryForm
            {
                Title = "Legado", Status = LibraryStatus.Playing, Rating = 7, Notes = "Manter notas",
                Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 7, PlatformId = 3 }]
            });
            pcOwnershipId = await scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().GameOwnerships
                .Where(x => x.LibraryEntryId == id && x.PlatformId == 1).Select(x => x.Id).SingleAsync();
        }
        var form = await Form(client, $"/Library/Edit/{id}"); SetValid(form);
        form["Notes"] = "Manter notas";
        form["SelectedPlatforms[1]"] = "xbox";
        var rejected = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, rejected.StatusCode);
        var html = Decode(await rejected.Content.ReadAsStringAsync());
        Assert.Contains("Confirme as plataformas que deseja remover antes de salvar.", html);
        Assert.Contains("Confirmar remoção</legend>", html);
        Assert.Contains("Tem certeza de que deseja remover PlayStation deste jogo?", html);
        Assert.Contains("Sim, desejo remover esta plataforma.", html);
        Assert.DoesNotContain("vínculos", html);
        Assert.DoesNotContain("posse genérica", html);
        Assert.Contains("Manter notas", html);
        var checkbox = Regex.Match(html, "<input type=\"checkbox\" name=\"SelectedPlatforms\"[^>]*id=\"platform-playstation\"[^>]*>").Value;
        Assert.DoesNotContain("checked=", checkbox);
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
            Assert.Equal(3, await db.GameOwnerships.CountAsync());
            Assert.Equal(7, (await db.LibraryEntries.SingleAsync()).Rating);
            Assert.False(await db.Stores.AnyAsync(x => x.Code == "unspecified"));
        }
        form["ConfirmedPlatformRemovals[0]"] = "playstation";
        var accepted = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.Redirect, accepted.StatusCode);
        using var verifyScope = app.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        var entry = await verify.LibraryEntries.Include(x => x.Ownerships).ThenInclude(x => x.Platform).SingleAsync();
        Assert.Equal(2, entry.Ownerships.Count);
        Assert.Contains(entry.Ownerships, x => x.Id == pcOwnershipId);
        Assert.Contains(entry.Ownerships, x => x.Platform.Code == "xbox");
        Assert.DoesNotContain(entry.Ownerships, x => PlatformGroups.FromPlatformCode(x.Platform.Code) == "playstation");
        Assert.Equal("Manter notas", entry.Notes);
        Assert.Equal(LibraryStatus.Playing, entry.Status);
        Assert.Equal(7, entry.Rating);
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    public async Task InvalidPlatformEditsDoNotRemoveExistingOwnerships(string error)
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        int id;
        using (var scope = app.Services.CreateScope())
            id = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
            { Title = "Manter", SelectedPlatforms = ["pc", "playstation"] });
        var form = await Form(client, $"/Library/Edit/{id}"); SetValid(form);
        form["ConfirmedPlatformRemovals[0]"] = "playstation";
        if (error == "empty") form.Remove("SelectedPlatforms[0]");
        if (error == "unknown") form["SelectedPlatforms[0]"] = "invalid";
        if (error == "duplicate") form["SelectedPlatforms[1]"] = "pc";
        var response = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("validation-summary-errors", await response.Content.ReadAsStringAsync());
        using var verifyScope = app.Services.CreateScope();
        var verify = verifyScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>();
        Assert.Equal(2, await verify.GameOwnerships.CountAsync());
        Assert.Null((await verify.LibraryEntries.SingleAsync()).Rating);
    }

    [Fact]
    public async Task MultipleRemovalsNameEachPlatformAndKeepEachConfirmationRequired()
    {
        using var app = new LibraryApplication(); using var client = Client(app);
        int id;
        using (var scope = app.Services.CreateScope())
            id = await scope.ServiceProvider.GetRequiredService<LibraryService>().SaveSimpleAsync(new SimpleLibraryForm
            { Title = "Duas plataformas", SelectedPlatforms = ["pc", "playstation"] });
        var form = await Form(client, $"/Library/Edit/{id}"); SetValid(form);
        form["SelectedPlatforms[0]"] = "xbox";
        var response = await client.PostAsync($"/Library/Edit/{id}", new FormUrlEncodedContent(form));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = Decode(await response.Content.ReadAsStringAsync());
        foreach (var group in new[] { (Code: "pc", Name: "PC"), (Code: "playstation", Name: "PlayStation") })
        {
            Assert.Contains($"Tem certeza de que deseja remover {group.Name} deste jogo?", html);
            var checkbox = Regex.Match(html, $"<input[^>]*id=\"remove-{group.Code}\"[^>]*>").Value;
            Assert.Contains("required=\"required\"", checkbox);
            Assert.DoesNotContain("disabled=", checkbox);
        }
        Assert.Equal(2, Regex.Matches(html, "Sim, desejo remover esta plataforma\\.").Count);
        using var verifyScope = app.Services.CreateScope();
        Assert.Equal(2, await verifyScope.ServiceProvider.GetRequiredService<NextGameAiDbContext>().GameOwnerships.CountAsync());
    }
}
