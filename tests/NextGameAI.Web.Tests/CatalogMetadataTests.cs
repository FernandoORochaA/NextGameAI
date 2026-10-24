using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;

namespace NextGameAI.Web.Tests;

public sealed class CatalogMetadataTests
{
    private const string Previous = "20261024195821_IgdbCatalogMetadata";
    private sealed class Fixture : IDisposable
    {
        public SqliteConnection Connection { get; } = new("Data Source=:memory:;Foreign Keys=True");
        public NextGameAiDbContext Db { get; }
        public Fixture(bool migrate = true)
        {
            Connection.Open();
            Db = new(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(Connection).Options);
            if (migrate) Db.Database.Migrate();
        }
        public async Task<Game> Game(long? id = null)
        {
            var game = new Game { Title = "Mesmo título", IgdbId = id };
            Db.Games.Add(game); await Db.SaveChangesAsync(); return game;
        }
        public void Dispose() { Db.Dispose(); Connection.Dispose(); }
    }

    [Theory]
    [InlineData(CatalogTaxonomyKind.Genre)] [InlineData(CatalogTaxonomyKind.Theme)]
    [InlineData(CatalogTaxonomyKind.GameMode)] [InlineData(CatalogTaxonomyKind.PlayerPerspective)]
    [InlineData(CatalogTaxonomyKind.Keyword)]
    public async Task TaxonomiesAreSharedAndExternalIdentityIsPreserved(CatalogTaxonomyKind kind)
    {
        using var f = new Fixture();
        var first = await f.Game(974); var second = await f.Game(132181);
        var common = new CatalogTaxonomy { Kind = kind, IgdbId = 12345, Name = "Nome externo" };
        var extra = new CatalogTaxonomy { Kind = kind, IgdbId = 67890, Name = "Outro" };
        f.Db.GameTaxonomies.AddRange(
            new() { Game = first, CatalogTaxonomy = common },
            new() { Game = first, CatalogTaxonomy = extra },
            new() { Game = second, CatalogTaxonomy = common });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        Assert.Equal(2, await f.Db.CatalogTaxonomies.CountAsync());
        Assert.Equal(3, await f.Db.GameTaxonomies.CountAsync());
        Assert.Equal(2, await f.Db.GameTaxonomies.CountAsync(g => g.GameId == first.Id));
        Assert.Equal(2, await f.Db.GameTaxonomies.CountAsync(g => g.CatalogTaxonomy.IgdbId == 12345));
        Assert.Equal(new long?[] { 974, 132181 }, await f.Db.Games.OrderBy(g => g.Id).Select(g => g.IgdbId).ToArrayAsync());
    }

    [Fact]
    public async Task KindScopesExternalIdsAndNamesAreNotIdentity()
    {
        using var f = new Fixture();
        f.Db.CatalogTaxonomies.AddRange(
            new() { Kind = CatalogTaxonomyKind.Genre, IgdbId = 1, Name = "Mesmo nome" },
            new() { Kind = CatalogTaxonomyKind.Theme, IgdbId = 1, Name = "Mesmo nome" },
            new() { Kind = CatalogTaxonomyKind.Genre, IgdbId = 2, Name = "Mesmo nome" });
        await f.Db.SaveChangesAsync();
        f.Db.CatalogTaxonomies.Add(new() { Kind = CatalogTaxonomyKind.Genre, IgdbId = 1, Name = "Traduzido" });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task DuplicateAssociationsAndOrphansAreRejectedBySqlite()
    {
        using var f = new Fixture(); var game = await f.Game();
        var tag = new CatalogTaxonomy { Kind = CatalogTaxonomyKind.Genre, IgdbId = 1, Name = "RPG" };
        f.Db.GameTaxonomies.Add(new() { Game = game, CatalogTaxonomy = tag }); await f.Db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO GameTaxonomies(GameId,CatalogTaxonomyId) VALUES({game.Id},{tag.Id})"));
        await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO GameTaxonomies(GameId,CatalogTaxonomyId) VALUES({game.Id},999999)"));
    }

    [Fact]
    public async Task ManualGamesNeedNoMetadataAndDurationCanBeUnknown()
    {
        using var f = new Fixture(); var manual = await f.Game(); var original = await f.Game(974);
        Assert.Null(manual.IgdbId); Assert.Empty(await f.Db.GameTimeToBeats.ToListAsync());
        f.Db.GameTimeToBeats.Add(new() { GameId = original.Id });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var time = await f.Db.GameTimeToBeats.SingleAsync();
        Assert.Null(time.HastilySeconds); Assert.Null(time.NormallySeconds);
        Assert.Null(time.CompletelySeconds); Assert.Null(time.SubmissionCount);
        Assert.Equal(original.Id, time.GameId);
        Assert.Equal(2, await f.Db.Games.CountAsync());
    }

    [Fact]
    public async Task DurationKeepsSecondsCountAndProvenanceOnCorrectVersion()
    {
        using var f = new Fixture(); var original = await f.Game(974); var remake = await f.Game(132181);
        f.Db.GameTimeToBeats.Add(new() { GameId = remake.Id, IgdbId = 123,
            HastilySeconds = 46900, NormallySeconds = 72669, CompletelySeconds = 155500, SubmissionCount = 17 });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var time = await f.Db.GameTimeToBeats.SingleAsync();
        Assert.Equal(remake.Id, time.GameId); Assert.Equal(46900, time.HastilySeconds);
        Assert.Equal(72669, time.NormallySeconds); Assert.Equal(155500, time.CompletelySeconds);
        Assert.Equal(17, time.SubmissionCount); Assert.Equal("IGDB", time.Source);
        Assert.False(await f.Db.GameTimeToBeats.AnyAsync(t => t.GameId == original.Id));
    }

    [Fact]
    public async Task MultiplayerIsExternalPerPlatformAndNullIsNotFalse()
    {
        using var f = new Fixture(); var game = await f.Game(17000);
        var pc = new CatalogPlatform { IgdbId = 6, Name = "PC" };
        var console = new CatalogPlatform { IgdbId = 130, Name = "Switch" };
        f.Db.GameMultiplayerModes.AddRange(
            new() { Game = game, CatalogPlatform = pc, OnlineCoop = true, OnlineCoopMax = 4, LanCoop = true },
            new() { Game = game, CatalogPlatform = console, OnlineCoop = false, OnlineCoopMax = 0 });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var modes = await f.Db.GameMultiplayerModes.Include(m => m.CatalogPlatform).OrderBy(m => m.CatalogPlatform!.IgdbId).ToArrayAsync();
        Assert.True(modes[0].OnlineCoop); Assert.Equal(4, modes[0].OnlineCoopMax);
        Assert.False(modes[1].OnlineCoop); Assert.Equal(0, modes[1].OnlineCoopMax);
        Assert.All(modes, m => { Assert.Null(m.OfflineCoop); Assert.Null(m.OfflineCoopMax);
            Assert.Null(m.CampaignCoop); Assert.Null(m.DropIn); Assert.Null(m.SplitScreen); Assert.Null(m.SplitScreenOnline); });
        Assert.Empty(await f.Db.GameOwnerships.ToListAsync());
        Assert.Equal(6, await f.Db.Platforms.CountAsync()); // Existing personal references are untouched.
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task MultiplayerRejectsDuplicateKnownOrUnknownPlatforms(bool known)
    {
        using var f = new Fixture(); var game = await f.Game();
        CatalogPlatform? platform = known ? new() { IgdbId = 6, Name = "PC" } : null;
        f.Db.GameMultiplayerModes.Add(new() { Game = game, CatalogPlatform = platform });
        await f.Db.SaveChangesAsync();
        f.Db.GameMultiplayerModes.Add(new() { Game = game, CatalogPlatform = platform });
        await Assert.ThrowsAsync<DbUpdateException>(() => f.Db.SaveChangesAsync());
    }

    [Fact]
    public async Task StateSeparatesNeverQueriedSuccessfulEmptyAndFailedAttempts()
    {
        using var f = new Fixture(); var game = await f.Game();
        Assert.Empty(await f.Db.GameEnrichmentStates.ToListAsync()); // Never queried.
        var old = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var state = new GameEnrichmentState { GameId = game.Id, Section = CatalogMetadataSection.Genres,
            LastAttemptAtUtc = old, LastSuccessAtUtc = old, LastResult = CatalogUpdateResult.Success };
        f.Db.GameEnrichmentStates.Add(state); await f.Db.SaveChangesAsync();
        Assert.Empty(await f.Db.GameTaxonomies.ToListAsync()); // Successful empty section.
        var tag = new CatalogTaxonomy { Kind = CatalogTaxonomyKind.Keyword, IgdbId = 9, Name = "Keyword não classificada" };
        f.Db.GameTaxonomies.Add(new() { Game = game, CatalogTaxonomy = tag });
        state.LastAttemptAtUtc = old.AddDays(40); state.LastResult = CatalogUpdateResult.TemporaryFailure;
        f.Db.GameEnrichmentStates.Add(new() { GameId = game.Id, Section = CatalogMetadataSection.TimeToBeat,
            LastAttemptAtUtc = old.AddDays(40), LastResult = CatalogUpdateResult.NotFound });
        await f.Db.SaveChangesAsync(); f.Db.ChangeTracker.Clear();
        var stored = await f.Db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        Assert.Equal(old, stored.LastSuccessAtUtc); Assert.Equal(old.AddDays(40), stored.LastAttemptAtUtc);
        Assert.Equal(CatalogUpdateResult.TemporaryFailure, stored.LastResult);
        Assert.Single(await f.Db.GameTaxonomies.ToListAsync()); // Old data is retained, no expiry deletion.
        Assert.Equal(2, await f.Db.GameEnrichmentStates.CountAsync());
    }

    [Theory]
    [InlineData("INSERT INTO CatalogTaxonomies(Kind,IgdbId,Name) VALUES(9,1,'x')")]
    [InlineData("INSERT INTO CatalogTaxonomies(Kind,IgdbId,Name) VALUES(0,0,'x')")]
    [InlineData("INSERT INTO CatalogTaxonomies(Kind,IgdbId,Name) VALUES(0,1,' ')")]
    [InlineData("INSERT INTO GameTimeToBeats(GameId,HastilySeconds,Source) SELECT Id,-1,'IGDB' FROM Games")]
    [InlineData("INSERT INTO GameEnrichmentStates(GameId,Section,LastAttemptAtUtc,LastResult,Source) SELECT Id,0,'2026-01-01',0,'IGDB' FROM Games")]
    [InlineData("INSERT INTO GameMultiplayerModes(GameId,OnlineCoopMax,Source) SELECT Id,-1,'IGDB' FROM Games")]
    public async Task DatabaseChecksRejectInvalidMetadata(string sql)
    {
        using var f = new Fixture(); await f.Game();
        await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.ExecuteSqlRawAsync(sql));
    }

    [Fact]
    public async Task ReferenceDeletionIsRestrictedAndNeverCascadesToPersonalData()
    {
        using var f = new Fixture(); var game = await f.Game(974);
        f.Db.LibraryEntries.Add(new() { Game = game, UserProfileId = 1, Rating = 7, Status = LibraryStatus.Playing,
            Notes = "Preservar", Ownerships = [new() { StoreId = 1, PlatformId = 1 }] });
        var tag = new CatalogTaxonomy { Kind = CatalogTaxonomyKind.Genre, IgdbId = 12, Name = "RPG" };
        f.Db.GameTaxonomies.Add(new() { Game = game, CatalogTaxonomy = tag }); await f.Db.SaveChangesAsync();
        f.Db.ChangeTracker.Clear();
        await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM CatalogTaxonomies WHERE Id={tag.Id}"));
        await Assert.ThrowsAsync<SqliteException>(() => f.Db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Games WHERE Id={game.Id}"));
        var entry = await f.Db.LibraryEntries.SingleAsync();
        Assert.Equal(7, entry.Rating); Assert.Equal("Preservar", entry.Notes);
        Assert.Equal(LibraryStatus.Playing, entry.Status); Assert.Single(await f.Db.GameOwnerships.ToListAsync());
    }

    [Fact]
    public void DesignTimeCannotSelectPersonalConnectionAndSnapshotMatches()
    {
        using var db = new NextGameAiDesignTimeFactory().CreateDbContext(["--connection", "personal.db"]);
        Assert.Equal("Data Source=:memory:;Foreign Keys=True", db.Database.GetConnectionString());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private static async Task<string> Dump(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table} ORDER BY Id";
        using var reader = await command.ExecuteReaderAsync();
        var rows = new List<object[]>();
        while (await reader.ReadAsync())
        {
            var values = new object[reader.FieldCount]; reader.GetValues(values); rows.Add(values);
        }
        return JsonSerializer.Serialize(rows);
    }

    [Fact]
    public async Task MigrationOnlyCreatesNewTablesAndPreservesEveryOldRowAndForeignKey()
    {
        using var f = new Fixture(false);
        var migrator = f.Db.GetService<IMigrator>(); await migrator.MigrateAsync(Previous);
        await f.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO Games(Id,Title,IgdbId) VALUES(10,'Original',974),(11,'Remake',132181),(12,'Manual',NULL);
            INSERT INTO LibraryEntries(Id,UserProfileId,GameId,Status,Rating,Notes,AbandonmentReason,AddedAtUtc,UpdatedAtUtc)
            VALUES(20,1,10,1,7,'Meia estrela',NULL,'2026-01-01','2026-01-01'),
                  (21,1,11,3,0,'Observação','Motivo opcional','2026-01-01','2026-01-01'),
                  (22,1,12,0,NULL,'Manual preservado',NULL,'2026-01-01','2026-01-01');
            INSERT INTO GameOwnerships(Id,LibraryEntryId,StoreId,PlatformId) VALUES(30,20,1,1),(31,20,4,3),(32,21,7,2),(33,22,1,1);
            """);
        string[] oldTables = ["Games", "LibraryEntries", "GameOwnerships", "UserProfiles", "Stores", "Platforms"];
        var before = new Dictionary<string, string>();
        foreach (var table in oldTables) before[table] = await Dump(f.Connection, table);
        await migrator.MigrateAsync();
        foreach (var table in oldTables) Assert.Equal(before[table], await Dump(f.Connection, table));
        Assert.Empty(await f.Db.GameTaxonomies.ToListAsync()); Assert.Empty(await f.Db.GameTimeToBeats.ToListAsync());
        Assert.Empty(await f.Db.GameMultiplayerModes.ToListAsync()); Assert.Empty(await f.Db.GameEnrichmentStates.ToListAsync());
        using var check = f.Connection.CreateCommand(); check.CommandText = "PRAGMA foreign_key_check";
        using (var reader = await check.ExecuteReaderAsync()) Assert.False(await reader.ReadAsync());
        var assembly = f.Db.GetService<IMigrationsAssembly>();
        var migration = assembly.CreateMigration(assembly.Migrations.Single(m => m.Key.EndsWith("_AddCatalogEnrichmentMetadata")).Value, f.Db.Database.ProviderName!);
        Assert.All(migration.UpOperations, op => Assert.True(op is CreateTableOperation or CreateIndexOperation));
        var created = migration.UpOperations.OfType<CreateTableOperation>().Select(o => o.Name).ToArray();
        Assert.Equal(6, created.Length); Assert.DoesNotContain(created, oldTables.Contains);
        Assert.All(migration.DownOperations, op => Assert.Contains(Assert.IsType<DropTableOperation>(op).Name, created));
        Assert.All(migration.UpOperations.OfType<CreateTableOperation>().SelectMany(t => t.ForeignKeys),
            fk => Assert.Equal(ReferentialAction.Restrict, fk.OnDelete));
        await migrator.MigrateAsync(Previous); // Down affects only the six new tables in this isolated database.
        foreach (var table in oldTables) Assert.Equal(before[table], await Dump(f.Connection, table));
    }
}
