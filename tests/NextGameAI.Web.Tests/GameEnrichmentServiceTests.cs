using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameEnrichmentServiceTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(-3));
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Metadata : IIgdbMetadataClient
    {
        public int Calls;
        public Func<long, IgdbMetadataResult> Response = Good;
        public Action? Before;
        public Task<IgdbMetadataResult> GetSectionsAsync(long id, IReadOnlyCollection<IgdbMetadataSectionKind> sections, CancellationToken ct = default) =>
            throw new InvalidOperationException("Existing persistence still uses the complete query.");
        public Task<IgdbMetadataResult> GetByIdAsync(long id, CancellationToken ct = default)
        {
            Calls++; ct.ThrowIfCancellationRequested(); Before?.Invoke();
            return Task.FromResult(Response(id));
        }
    }
    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new(TestDatabaseGuard.ConnectionString);
        public Clock Clock { get; } = new();
        public Metadata Metadata { get; } = new();
        public ServiceProvider Root { get; }
        public IGameEnrichmentService Service { get; }
        private readonly TestDatabaseGuard guard;
        public Fixture()
        {
            guard = new(Connection); guard.Validate(Connection); Connection.Open();
            var services = new ServiceCollection();
            services.AddDbContext<NextGameAiDbContext>(o => o.UseSqlite(Connection).AddInterceptors(guard));
            services.AddSingleton<TimeProvider>(Clock); services.AddSingleton<IIgdbMetadataClient>(Metadata);
            services.AddScoped<IGameEnrichmentService, GameEnrichmentService>();
            Root = services.BuildServiceProvider(validateScopes: true);
            // The operation uses scopes of its own; never saves a caller's pending context.
            Service = new GameEnrichmentService(Root.GetRequiredService<IServiceScopeFactory>(), Metadata, Clock);
            using var db = Db(); db.Database.Migrate();
        }
        public NextGameAiDbContext Db() => new(new DbContextOptionsBuilder<NextGameAiDbContext>()
            .UseSqlite(Connection).AddInterceptors(guard).Options);
        public async Task<int> Game(long? id = 974)
        {
            using var db = Db(); var game = new Game { IgdbId = id, Title = "Synthetic original", EditionTitle = "Synthetic edition" };
            db.Games.Add(game); await db.SaveChangesAsync(); return game.Id;
        }
        public async Task<string> Snapshot(params string[] tables)
        {
            var values = new List<object>();
            foreach (var table in tables)
            {
                using var command = Connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                using var reader = await command.ExecuteReaderAsync(); var rows = new List<object[]>();
                while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
                values.Add(rows);
            }
            return JsonSerializer.Serialize(values);
        }
        public void Dispose() { Root.Dispose(); Connection.Dispose(); }
    }

    private static IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Section(IgdbTaxonomyKind kind, params long[] ids) =>
        new(IgdbMetadataStatus.Success, Array.AsReadOnly(ids.Select(id => new IgdbTaxonomyResult(kind, id, "Synthetic reference " + id)).ToArray()));
    private static IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Error(IgdbMetadataStatus status) => new(status);
    private static IgdbMetadataResult Good(long id) => new(id,
        Section(IgdbTaxonomyKind.Genre, 10), Section(IgdbTaxonomyKind.Theme, 10), Section(IgdbTaxonomyKind.GameMode, 10),
        Section(IgdbTaxonomyKind.PlayerPerspective, 10), Section(IgdbTaxonomyKind.Keyword, 10),
        new(IgdbMetadataStatus.Success, new(99, id, 1, 2, 3, 4)),
        new(IgdbMetadataStatus.Success, Array.AsReadOnly(new[] { new IgdbMultiplayerResult(1, id,
            new(6, "Synthetic PC"), true, false, null, false, true, null, null, 0, null, 4, null) })));

    [Fact]
    public async Task CreatesFiveKindsWithScopedIdentityUtcAndNoDurationOrMultiplayerWrites()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Before = () =>
        {
            using var command = f.Connection.CreateCommand();
            command.CommandText = "BEGIN; ROLLBACK;"; command.ExecuteNonQuery(); // No transaction across HTTP.
        };
        var result = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome);
        using var db = f.Db(); var refs = await db.CatalogTaxonomies.OrderBy(t => t.Kind).ToArrayAsync();
        Assert.Equal(Enum.GetValues<CatalogTaxonomyKind>(), refs.Select(t => t.Kind));
        Assert.All(refs, r => Assert.Equal(10, r.IgdbId));
        Assert.Equal(5, await db.GameTaxonomies.CountAsync());
        var states = await db.GameEnrichmentStates.ToArrayAsync(); Assert.Equal(5, states.Length);
        Assert.All(states, s => { Assert.Equal(CatalogUpdateResult.Success, s.LastResult); Assert.Equal(f.Clock.Now.UtcDateTime, s.LastAttemptAtUtc); Assert.Equal(s.LastAttemptAtUtc, s.LastSuccessAtUtc); Assert.Equal("IGDB", s.Source); });
        Assert.Empty(await db.GameTimeToBeats.ToArrayAsync()); Assert.Empty(await db.GameMultiplayerModes.ToArrayAsync());
        Assert.Empty(await db.CatalogPlatforms.ToArrayAsync());
        Assert.DoesNotContain(typeof(CatalogGameResult).GetProperties(), p => p.Name.Contains("Keyword"));
    }

    [Fact]
    public async Task SharedTaxonomiesStayUniqueAndRepeatedPayloadKeepsLinkIds()
    {
        using var f = new Fixture(); var original = await f.Game(); var remake = await f.Game(132181);
        await f.Service.EnrichTaxonomiesAsync(original); await f.Service.EnrichTaxonomiesAsync(remake);
        var before = await f.Snapshot("CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates");
        await f.Service.EnrichTaxonomiesAsync(original);
        Assert.Equal(before, await f.Snapshot("CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates"));
        using var db = f.Db(); Assert.Equal(5, await db.CatalogTaxonomies.CountAsync()); Assert.Equal(10, await db.GameTaxonomies.CountAsync());
        Assert.Equal(new long?[] { 974, 132181 }, await db.Games.OrderBy(g => g.Id).Select(g => g.IgdbId).ToArrayAsync());
    }

    [Fact]
    public async Task SyncRemovesOnlyObsoleteLinksAndRenamesSharedReferenceWithoutDeletingIt()
    {
        using var f = new Fixture(); var first = await f.Game(); var other = await f.Game(132181);
        await f.Service.EnrichTaxonomiesAsync(first); await f.Service.EnrichTaxonomiesAsync(other);
        f.Metadata.Response = id => Good(id) with
        {
            Genres = new(IgdbMetadataStatus.Success, Array.AsReadOnly(new[] { new IgdbTaxonomyResult(IgdbTaxonomyKind.Genre, 20, "Synthetic new") })),
            Themes = new(IgdbMetadataStatus.Success, Array.AsReadOnly(new[] { new IgdbTaxonomyResult(IgdbTaxonomyKind.Theme, 10, "Synthetic renamed") }))
        };
        await f.Service.EnrichTaxonomiesAsync(first);
        using var db = f.Db(); var links = await db.GameTaxonomies.Include(l => l.CatalogTaxonomy).ToListAsync();
        Assert.Contains(links, l => l.GameId == first && l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 20);
        Assert.DoesNotContain(links, l => l.GameId == first && l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 10);
        Assert.Contains(links, l => l.GameId == other && l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 10);
        Assert.Equal(4, links.Count(l => l.GameId == first && l.CatalogTaxonomy.Kind != CatalogTaxonomyKind.Genre));
        Assert.Equal(5, links.Count(l => l.GameId == other)); Assert.Equal(6, await db.CatalogTaxonomies.CountAsync());
        Assert.Equal("Synthetic renamed", (await db.CatalogTaxonomies.SingleAsync(t => t.Kind == CatalogTaxonomyKind.Theme)).Name);
    }

    [Theory]
    [InlineData(IgdbMetadataStatus.InvalidResponse)]
    [InlineData(IgdbMetadataStatus.TransportFailure)]
    [InlineData(IgdbMetadataStatus.GameNotFound)]
    public async Task FailedSectionPreservesContentAndLastSuccessWhileOtherSectionUpdates(IgdbMetadataStatus status)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        f.Clock.Now = f.Clock.Now.AddHours(1);
        f.Metadata.Response = external => Good(external) with { Genres = Error(status), Themes = Section(IgdbTaxonomyKind.Theme, 30) };
        var r = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Partial, r.Outcome);
        using var db = f.Db(); var state = await db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        Assert.Equal(status == IgdbMetadataStatus.GameNotFound ? CatalogUpdateResult.NotFound : CatalogUpdateResult.TemporaryFailure, state.LastResult);
        Assert.Equal(f.Clock.Now.UtcDateTime, state.LastAttemptAtUtc); Assert.Equal(f.Clock.Now.AddHours(-1).UtcDateTime, state.LastSuccessAtUtc);
        Assert.True(await db.GameTaxonomies.AnyAsync(l => l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 10));
        Assert.True(await db.GameTaxonomies.AnyAsync(l => l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Theme && l.CatalogTaxonomy.IgdbId == 30));
        Assert.Equal(status, r.Sections.Single(s => s.Section == CatalogMetadataSection.Genres).ExternalStatus);
    }

    [Fact]
    public async Task EmptyContractWithoutCompletenessEvidenceNeverDeletesOrClaimsSuccess()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        f.Metadata.Response = external => Good(external) with { Genres = new(IgdbMetadataStatus.Empty, Array.AsReadOnly(Array.Empty<IgdbTaxonomyResult>())) };
        var r = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(TaxonomyEnrichmentOutcome.EmptyPreserved, r.Sections.Single(s => s.Section == CatalogMetadataSection.Genres).Outcome);
        using var db = f.Db(); Assert.True(await db.GameTaxonomies.AnyAsync(l => l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre));
        Assert.Equal(CatalogUpdateResult.TemporaryFailure, (await db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres)).LastResult);
    }

    [Theory]
    [InlineData("kind")] [InlineData("zero")] [InlineData("negative")] [InlineData("name")]
    [InlineData("long")] [InlineData("control")] [InlineData("duplicate")] [InlineData("null")]
    [InlineData("success-empty")] [InlineData("contradiction")]
    public async Task ValidatesCompleteSectionBeforeAnyAssociationMutation(string invalid)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        var rows = new List<IgdbTaxonomyResult> { new(IgdbTaxonomyKind.Genre, 20, "Synthetic valid") };
        rows.Add(invalid switch
        {
            "kind" => new(IgdbTaxonomyKind.Theme, 30, "Synthetic"),
            "zero" => new(IgdbTaxonomyKind.Genre, 0, "Synthetic"),
            "negative" => new(IgdbTaxonomyKind.Genre, -1, "Synthetic"),
            "name" => new(IgdbTaxonomyKind.Genre, 30, " "),
            "long" => new(IgdbTaxonomyKind.Genre, 30, new string('x', 501)),
            "control" => new(IgdbTaxonomyKind.Genre, 30, "Synthetic\n"),
            "duplicate" => new(IgdbTaxonomyKind.Genre, 20, "Synthetic duplicate"),
            _ => new(IgdbTaxonomyKind.Genre, 30, "Synthetic")
        });
        var section = new IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>>(IgdbMetadataStatus.Success,
            invalid == "null" ? null : invalid == "success-empty" ? Array.Empty<IgdbTaxonomyResult>() : rows,
            invalid == "contradiction" ? IgdbQueryFailure.InvalidResponse : null);
        f.Metadata.Response = external => Good(external) with { Genres = section };
        var result = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(TaxonomyEnrichmentOutcome.InvalidResponse, result.Sections.Single(s => s.Section == CatalogMetadataSection.Genres).Outcome);
        using var db = f.Db(); Assert.Equal(5, await db.CatalogTaxonomies.CountAsync());
        Assert.Single(await db.GameTaxonomies.Where(l => l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 10).ToListAsync());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MissingOrManualGameDoesNotCallMetadataOrWrite(bool manual)
    {
        using var f = new Fixture(); var id = manual ? await f.Game(null) : 999;
        var before = await f.Snapshot("Games", "CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates");
        Assert.Equal(manual ? GameEnrichmentOutcome.MissingIgdbId : GameEnrichmentOutcome.GameNotFound, (await f.Service.EnrichTaxonomiesAsync(id)).Outcome);
        Assert.Equal(0, f.Metadata.Calls); Assert.Equal(before, await f.Snapshot("Games", "CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MismatchedResultOrChangedLocalIdentityCannotWrite(bool changeLocal)
    {
        using var f = new Fixture(); var id = await f.Game();
        if (changeLocal) f.Metadata.Before = () => { using var db = f.Db(); db.Games.Single().IgdbId = 132181; db.SaveChanges(); };
        else f.Metadata.Response = _ => Good(132181);
        Assert.Equal(GameEnrichmentOutcome.IdentityMismatch, (await f.Service.EnrichTaxonomiesAsync(id)).Outcome);
        using var read = f.Db(); Assert.Empty(await read.CatalogTaxonomies.ToListAsync()); Assert.Empty(await read.GameEnrichmentStates.ToListAsync());
    }

    [Fact]
    public async Task WriteFailureRollsBackReferencesLinksNamesAndStatesTogether()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        var before = await f.Snapshot("CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates");
        using (var command = f.Connection.CreateCommand())
        {
            command.CommandText = "CREATE TRIGGER FailState BEFORE UPDATE ON GameEnrichmentStates WHEN NEW.Section=4 BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;";
            command.ExecuteNonQuery();
        }
        f.Clock.Now = f.Clock.Now.AddHours(1);
        f.Metadata.Response = external => Good(external) with { Genres = Section(IgdbTaxonomyKind.Genre, 20) };
        var r = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, r.Outcome); Assert.Empty(r.Sections);
        Assert.Equal(before, await f.Snapshot("CatalogTaxonomies", "GameTaxonomies", "GameEnrichmentStates"));
    }

    [Fact]
    public async Task PreservesEveryPersonalColumnAndDoesNotSaveCallersPendingChanges()
    {
        using var f = new Fixture(); var id = await f.Game(); var other = await f.Game(132181);
        using (var db = f.Db())
        {
            db.LibraryEntries.AddRange(new LibraryEntry { GameId = id, UserProfileId = 1, Rating = 0, Notes = "Synthetic private", Status = LibraryStatus.Playing,
                Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 2, PlatformId = 1 }] },
                new LibraryEntry { GameId = other, UserProfileId = 1, Rating = 7, Status = LibraryStatus.Abandoned, AbandonmentReason = "Synthetic reason" });
            await db.SaveChangesAsync();
        }
        string[] tables = ["Games", "LibraryEntries", "GameOwnerships", "UserProfiles", "Stores", "Platforms", "GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms"];
        var before = await f.Snapshot(tables);
        using var caller = f.Db(); var tracked = await caller.Games.SingleAsync(g => g.Id == id); tracked.Title = "Do not save";
        await f.Service.EnrichTaxonomiesAsync(id);
        Assert.Equal(before, await f.Snapshot(tables));
        Assert.Equal(EntityState.Modified, caller.Entry(tracked).State);
    }

    [Fact]
    public async Task QueryFailureIsControlledAndCancellationNeverWritesStates()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Response = _ => throw new IgdbQueryException(IgdbQueryFailure.Timeout);
        var result = await f.Service.EnrichTaxonomiesAsync(id); Assert.Equal(GameEnrichmentOutcome.Preserved, result.Outcome);
        Assert.All(result.Sections, s => Assert.Equal(IgdbQueryFailure.Timeout, s.Failure));
        using var db = f.Db(); Assert.All(await db.GameEnrichmentStates.ToListAsync(), s => Assert.Null(s.LastSuccessAtUtc));
        var before = await f.Snapshot("GameEnrichmentStates");
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.EnrichTaxonomiesAsync(id, cts.Token));
        Assert.Equal(before, await f.Snapshot("GameEnrichmentStates"));
    }
    [Fact]
    public async Task NotRequestedTaxonomyDoesNotChangeItsLinksOrTimestamps()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        GameEnrichmentState previous;
        using (var db = f.Db()) previous = await db.GameEnrichmentStates.AsNoTracking().SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        f.Clock.Now = f.Clock.Now.AddDays(1);
        f.Metadata.Response = external => Good(external) with { Genres = new(IgdbMetadataStatus.NotRequested) };
        var r = await f.Service.EnrichTaxonomiesAsync(id);
        Assert.DoesNotContain(r.Sections, s => s.Section == CatalogMetadataSection.Genres);
        using var read = f.Db(); var state = await read.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        Assert.Equal(previous.LastAttemptAtUtc, state.LastAttemptAtUtc); Assert.Equal(previous.LastSuccessAtUtc, state.LastSuccessAtUtc);
        Assert.Equal(CatalogUpdateResult.Success, state.LastResult);
        Assert.True(await read.GameTaxonomies.AnyAsync(l => l.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre && l.CatalogTaxonomy.IgdbId == 10));
    }

    [Fact]
    public async Task EntirelyNotRequestedTaxonomiesNeverCreateOrUpdateStates()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Response = external => Good(external) with
        {
            Genres = new(IgdbMetadataStatus.NotRequested), Themes = new(IgdbMetadataStatus.NotRequested),
            GameModes = new(IgdbMetadataStatus.NotRequested), PlayerPerspectives = new(IgdbMetadataStatus.NotRequested), Keywords = new(IgdbMetadataStatus.NotRequested)
        };
        Assert.Equal(GameEnrichmentOutcome.Preserved, (await f.Service.EnrichTaxonomiesAsync(id)).Outcome);
        using var db = f.Db(); Assert.Empty(await db.GameEnrichmentStates.ToArrayAsync()); Assert.Empty(await db.GameTaxonomies.ToArrayAsync());
    }

}
