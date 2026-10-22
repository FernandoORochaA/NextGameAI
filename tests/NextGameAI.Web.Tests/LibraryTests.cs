using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Tests;

public sealed class LibraryTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly NextGameAiDbContext db;
    private readonly LibraryService library;

    public LibraryTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        library = new(db, new(db));
    }

    private static LibraryForm Valid() => new()
    {
        Title = "Jogo de teste", EditionTitle = "Original", Status = LibraryStatus.NotStarted,
        Ownerships = [new() { StoreId = 1, PlatformId = 1 }]
    };

    [Fact]
    public async Task CreatesValidEntryAndReadsFromNewContext()
    {
        var id = await library.SaveAsync(Valid());
        using var read = new NextGameAiDbContext(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).Options);
        var entry = await read.LibraryEntries.Include(x => x.Game).Include(x => x.Ownerships).SingleAsync();
        Assert.Equal(id, entry.Id);
        Assert.Equal("Jogo de teste", entry.Game.Title);
        Assert.Null(entry.Game.IgdbId);
        Assert.Single(entry.Ownerships);
        Assert.Equal(LocalProfile.Id, entry.UserProfileId);
        Assert.True(entry.AddedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Theory]
    [InlineData(null)] [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(10)]
    public async Task PreservesUnratedZeroAndHalfStars(int? rating)
    {
        var input = Valid(); input.Rating = rating;
        var id = await library.SaveAsync(input);
        db.ChangeTracker.Clear();
        Assert.Equal(rating, (await db.LibraryEntries.FindAsync(id))!.Rating);
    }

    [Theory]
    [InlineData(-1)] [InlineData(11)]
    public async Task RejectsOutOfRangeRatingWithoutPartialCatalog(int rating)
    {
        var input = Valid(); input.Rating = rating;
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
        Assert.Empty(await db.LibraryEntries.ToListAsync());
    }

    [Fact]
    public async Task RequiresOwnership()
    {
        var input = Valid(); input.Ownerships.Clear();
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(1, 0)] [InlineData(999, 1)] [InlineData(1, 999)]
    public async Task RejectsMissingOrUnknownReferences(int storeId, int platformId)
    {
        var input = Valid(); input.Ownerships = [new() { StoreId = storeId, PlatformId = platformId }];
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task SupportsMultipleOwnershipsAndAtomicReplacement()
    {
        var input = Valid();
        input.Ownerships.Add(new() { StoreId = 2, PlatformId = 1 });
        var id = await library.SaveAsync(input);
        var entry = await db.LibraryEntries.FindAsync(id);
        Assert.Equal(2, await db.GameOwnerships.CountAsync());
        input.ExistingGameId = entry!.GameId;
        input.Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 4, PlatformId = 3 }];
        await library.SaveAsync(input, id);
        Assert.Equal(2, await db.GameOwnerships.CountAsync());
        Assert.Single(await db.Games.ToListAsync());
        Assert.True(await db.GameOwnerships.AnyAsync(x => x.StoreId == 4 && x.PlatformId == 3));
    }

    [Fact]
    public async Task RejectsDuplicateOwnerships()
    {
        var input = Valid(); input.Ownerships.Add(new() { StoreId = 1, PlatformId = 1 });
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task ReusesCatalogAndRejectsDuplicateLibraryEntry()
    {
        var game = new Game { Title = "Catálogo existente" }; db.Games.Add(game); await db.SaveChangesAsync();
        var input = Valid(); input.ExistingGameId = game.Id; input.Title = null;
        await library.SaveAsync(input);
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Single(await db.Games.ToListAsync());
        Assert.Single(await db.LibraryEntries.ToListAsync());
    }

    [Fact]
    public async Task SameTitleDoesNotMergeDistinctCatalogRecords()
    {
        await library.SaveAsync(Valid()); await library.SaveAsync(Valid());
        Assert.Equal(2, await db.Games.CountAsync());
    }

    [Theory]
    [InlineData(LibraryStatus.NotStarted)] [InlineData(LibraryStatus.Playing)]
    [InlineData(LibraryStatus.Completed)] [InlineData(LibraryStatus.Abandoned)]
    public async Task TransitionsFromAbandonedAndClearsReasonWhenAppropriate(LibraryStatus next)
    {
        var input = Valid(); input.Status = LibraryStatus.Abandoned; input.AbandonmentReason = "Não gostei";
        var id = await library.SaveAsync(input);
        input.ExistingGameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        input.Status = next;
        await library.SaveAsync(input, id);
        db.ChangeTracker.Clear();
        var entry = await db.LibraryEntries.FindAsync(id);
        Assert.Equal(next, entry!.Status);
        Assert.Equal(next == LibraryStatus.Abandoned ? "Não gostei" : null, entry.AbandonmentReason);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public async Task RejectsTextOverflow(int field)
    {
        var input = Valid();
        switch (field)
        {
            case 0: input.Title = new('x', 301); break;
            case 1: input.EditionTitle = new('x', 201); break;
            case 2: input.Notes = new('x', 5001); break;
            case 3: input.Status = LibraryStatus.Abandoned; input.AbandonmentReason = new('x', 1001); break;
            case 4: input.Title = "   "; break;
        }
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task AcceptsTextLimitsAndAbandonmentWithoutReason()
    {
        var input = Valid(); input.Title = new('x', 300); input.EditionTitle = new('x', 200);
        input.Notes = new('x', 5000); input.Status = LibraryStatus.Abandoned;
        input.AbandonmentReason = new('x', 1000);
        var id = await library.SaveAsync(input);
        input.ExistingGameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        input.AbandonmentReason = null;
        await library.SaveAsync(input, id);
        Assert.Null((await db.LibraryEntries.FindAsync(id))!.AbandonmentReason);
    }

    [Fact]
    public async Task InvalidEditLeavesExistingDataIntact()
    {
        var id = await library.SaveAsync(Valid());
        var input = Valid(); input.ExistingGameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        input.Status = LibraryStatus.Completed; input.Ownerships = [new() { StoreId = 999, PlatformId = 1 }];
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input, id));
        db.ChangeTracker.Clear();
        Assert.Equal(LibraryStatus.NotStarted, (await db.LibraryEntries.FindAsync(id))!.Status);
        Assert.Equal(1, (await db.GameOwnerships.SingleAsync()).StoreId);
    }

    [Fact]
    public async Task CatalogCannotBeReplacedDuringEdit()
    {
        var id = await library.SaveAsync(Valid());
        var input = Valid(); input.ExistingGameId = 999;
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input, id));
        Assert.Single(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task DeletePreservesSharedCatalogAndReferences()
    {
        var id = await library.SaveAsync(Valid());
        var gameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        db.UserProfiles.Add(new() { Id = 2, DisplayName = "Outro perfil" });
        db.LibraryEntries.Add(new() { UserProfileId = 2, GameId = gameId });
        await db.SaveChangesAsync();
        Assert.True(await library.DeleteAsync(id));
        db.ChangeTracker.Clear();
        Assert.Empty(await db.GameOwnerships.ToListAsync());
        Assert.Single(await db.Games.ToListAsync());
        Assert.Single(await db.LibraryEntries.ToListAsync());
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Fact]
    public async Task OtherProfileEntriesAreNotAccessible()
    {
        db.UserProfiles.Add(new() { Id = 2, DisplayName = "Outro perfil" });
        var other = new LibraryEntry { UserProfileId = 2, Game = new() { Title = "Outro jogo" } };
        db.LibraryEntries.Add(other); await db.SaveChangesAsync();
        Assert.Empty(await (await library.QueryAsync()).ToListAsync());
        Assert.False(await library.DeleteAsync(other.Id));
        var input = Valid(); input.ExistingGameId = other.GameId;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => library.SaveAsync(input, other.Id));
    }

    [Fact]
    public async Task SqliteEnforcesUniqueEntryAndOwnership()
    {
        var id = await library.SaveAsync(Valid());
        var gameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO LibraryEntries (UserProfileId, GameId, Status, AddedAtUtc, UpdatedAtUtc) VALUES ({LocalProfile.Id}, {gameId}, 0, '2026-01-01', '2026-01-01')"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"INSERT INTO GameOwnerships (LibraryEntryId, StoreId, PlatformId) VALUES ({id}, 1, 1)"));
    }

    [Theory]
    [InlineData("Rating", "-1")] [InlineData("Rating", "11")] [InlineData("Status", "4")]
    [InlineData("AbandonmentReason", "indevido")]
    public async Task SqliteCheckConstraintsRejectInvalidValues(string column, string value)
    {
        var id = await library.SaveAsync(Valid());
        var sql = column switch
        {
            "Rating" => "UPDATE LibraryEntries SET Rating = {0} WHERE Id = {1}",
            "Status" => "UPDATE LibraryEntries SET Status = {0} WHERE Id = {1}",
            _ => "UPDATE LibraryEntries SET AbandonmentReason = {0} WHERE Id = {1}"
        };
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync(sql, value, id));
    }

    [Fact]
    public async Task SqliteEnforcesIgdbUniquenessButAllowsSeveralNulls()
    {
        db.Games.AddRange(new Game { Title = "A" }, new Game { Title = "B" }, new Game { Title = "IGDB", IgdbId = 123 });
        await db.SaveChangesAsync();
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO Games (Title, IgdbId) VALUES ('Duplicado', 123)"));
        await db.Database.ExecuteSqlRawAsync("UPDATE Platforms SET IgdbId = 123 WHERE Id = 1");
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("UPDATE Platforms SET IgdbId = 123 WHERE Id = 2"));
    }

    [Fact]
    public async Task SqliteEnforcesReferenceCodesAndRestrictsCatalogDeletion()
    {
        var id = await library.SaveAsync(Valid());
        var gameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO Stores (Code, Name) VALUES ('steam', 'Outra')"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("INSERT INTO Platforms (Code, Name) VALUES ('pc', 'Outra')"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM Games WHERE Id = {gameId}"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM Stores WHERE Id = 1"));
        await Assert.ThrowsAsync<SqliteException>(() => db.Database.ExecuteSqlRawAsync("DELETE FROM Platforms WHERE Id = 1"));
    }

    [Fact]
    public async Task DbFailureRollsBackNewCatalogAndEntry()
    {
        // Force failure after EF has inserted the catalog row, verifying the whole transaction rolls back.
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOwnership BEFORE INSERT ON GameOwnerships BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => library.SaveAsync(Valid()));
        Assert.Empty(await db.Games.ToListAsync());
        Assert.Empty(await db.LibraryEntries.ToListAsync());
        Assert.Empty(await db.GameOwnerships.ToListAsync());
    }

    [Fact]
    public async Task DbFailureDuringEditRestoresStatusAndPreviousOwnership()
    {
        var id = await library.SaveAsync(Valid());
        var input = Valid(); input.ExistingGameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        input.Status = LibraryStatus.Completed;
        input.Ownerships = [new() { StoreId = 2, PlatformId = 1 }];
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOwnership BEFORE INSERT ON GameOwnerships BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => library.SaveAsync(input, id));
        Assert.Equal(LibraryStatus.NotStarted, (await db.LibraryEntries.FindAsync(id))!.Status);
        Assert.Equal(1, (await db.GameOwnerships.SingleAsync()).StoreId);
    }

    [Fact]
    public async Task RejectsUndefinedStatusBeforeWriting()
    {
        var input = Valid(); input.Status = (LibraryStatus)99;
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task EntityValidationEnforcesReferenceTextLimits()
    {
        db.Stores.Add(new() { Code = new('a', 51), Name = "Store" });
        await Assert.ThrowsAsync<ValidationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.Platforms.Add(new() { Code = "test", Name = new('a', 101) });
        await Assert.ThrowsAsync<ValidationException>(() => db.SaveChangesAsync());
        db.ChangeTracker.Clear();
        db.UserProfiles.Add(new() { DisplayName = new('a', 101) });
        await Assert.ThrowsAsync<ValidationException>(() => db.SaveChangesAsync());
    }

    [Fact]
    public async Task MigrationsAndSeedAreRepeatable()
    {
        await db.Database.MigrateAsync();
        Assert.Single(await db.UserProfiles.ToListAsync());
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
        Assert.Empty(await db.Games.ToListAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
