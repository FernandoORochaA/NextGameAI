using System.ComponentModel.DataAnnotations;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.ViewModels;

namespace NextGameAI.Web.Tests;

public sealed class SimpleLibraryTests : IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:;Foreign Keys=True");
    private readonly NextGameAiDbContext db;
    private readonly LibraryService library;

    public SimpleLibraryTests()
    {
        connection.Open();
        db = new(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection).Options);
        db.Database.Migrate();
        library = new(db, new(db));
    }

    private static SimpleLibraryForm Form(params string[] platforms) => new()
    {
        Title = "Jogo simples", SelectedPlatforms = platforms.ToList()
    };

    [Fact]
    public async Task CreatesAllGroupsWithoutInventingCommercialOwnershipOrConsoleGeneration()
    {
        var id = await library.SaveSimpleAsync(Form("pc", "playstation", "nintendo", "xbox", "other"));
        db.ChangeTracker.Clear();
        var entry = await (await library.QueryAsync()).SingleAsync(x => x.Id == id);
        Assert.Equal(5, entry.Ownerships.Count);
        Assert.All(entry.Ownerships, x => Assert.Equal("unspecified", x.Store.Code));
        Assert.Equal(5, entry.Ownerships.Select(x => x.Platform.Code).Distinct().Count());
        Assert.Contains(entry.Ownerships, x => x.PlatformId == 1 && x.Platform.Code == "pc");
        Assert.DoesNotContain(entry.Ownerships, x => x.Platform.Code is "ps5" or "switch" or "xbox-series");
        Assert.Equal(7, await db.Stores.CountAsync(x => x.Code != "unspecified"));
        Assert.Null(entry.Game.EditionTitle);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Theory]
    [InlineData(null)] [InlineData(0)] [InlineData(1)] [InlineData(7)] [InlineData(10)]
    public async Task SavesAndEditsUnratedZeroAndHalfStars(int? rating)
    {
        var input = Form("pc"); input.Rating = rating;
        var id = await library.SaveSimpleAsync(input);
        Assert.Equal(rating, (await db.LibraryEntries.FindAsync(id))!.Rating);
        input.SelectedPlatforms.Add("xbox");
        await library.SaveSimpleAsync(input, id);
        db.ChangeTracker.Clear();
        Assert.Equal(rating, (await db.LibraryEntries.FindAsync(id))!.Rating);
    }

    [Theory]
    [InlineData("missing")] [InlineData("duplicate")] [InlineData("unknown")]
    [InlineData("rating-negative")] [InlineData("rating-overflow")] [InlineData("status")]
    [InlineData("title")] [InlineData("notes")] [InlineData("reason")]
    public async Task InvalidInputDoesNotCreateReferencesOrData(string error)
    {
        var input = Form("playstation");
        switch (error)
        {
            case "missing": input.SelectedPlatforms.Clear(); break;
            case "duplicate": input.SelectedPlatforms.Add("playstation"); break;
            case "unknown": input.SelectedPlatforms = ["steam"]; break;
            case "rating-negative": input.Rating = -1; break;
            case "rating-overflow": input.Rating = 11; break;
            case "status": input.Status = (LibraryStatus)9; break;
            case "title": input.Title = new('x', 301); break;
            case "notes": input.Notes = new('x', 5001); break;
            case "reason": input.Status = LibraryStatus.Abandoned; input.AbandonmentReason = new('x', 1001); break;
        }
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveSimpleAsync(input));
        Assert.Empty(await db.Games.ToListAsync());
        Assert.Empty(await db.GameOwnerships.ToListAsync());
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Fact]
    public async Task EditRequiresConfirmationForOmittedGroupsAndPreservesMaintainedDetails()
    {
        var game = new Game { Title = "Original", EditionTitle = "Edição preservada", IgdbId = 987 };
        db.Games.Add(game); await db.SaveChangesAsync();
        var id = await library.SaveAsync(new LibraryForm
        {
            ExistingGameId = game.Id,
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 2, PlatformId = 1 },
                new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 4, PlatformId = 3 }]
        });
        var oldRows = await db.GameOwnerships.AsNoTracking().ToListAsync();
        var oldAdded = (await db.LibraryEntries.FindAsync(id))!.AddedAtUtc;
        var input = Form("xbox"); input.Title = "Não renomear catálogo"; input.Rating = 7;
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveSimpleAsync(input, id));
        Assert.Equal(4, await db.GameOwnerships.CountAsync());
        input.SelectedPlatforms = ["pc", "playstation", "xbox"];
        await library.SaveSimpleAsync(input, id);
        db.ChangeTracker.Clear();
        var entry = await (await library.QueryAsync()).SingleAsync();
        Assert.Equal(5, entry.Ownerships.Count);
        foreach (var old in oldRows)
            Assert.Contains(entry.Ownerships, x => x.Id == old.Id && x.StoreId == old.StoreId && x.PlatformId == old.PlatformId);
        Assert.Equal("Original", entry.Game.Title);
        Assert.Equal("Edição preservada", entry.Game.EditionTitle);
        Assert.Equal(987, entry.Game.IgdbId);
        Assert.Equal(game.Id, entry.GameId);
        Assert.Equal(oldAdded, entry.AddedAtUtc);
        Assert.Equal("unspecified", entry.Ownerships.Single(x => x.Platform.Code == "xbox").Store.Code);
        Assert.DoesNotContain(entry.Ownerships, x => x.Platform.Code == "playstation");
    }

    [Fact]
    public async Task RepeatedEditsDoNotDuplicateReferencesOrOwnerships()
    {
        var input = Form("pc", "playstation");
        var id = await library.SaveSimpleAsync(input);
        var oldIds = await db.GameOwnerships.Select(x => x.Id).OrderBy(x => x).ToListAsync();
        await library.SaveSimpleAsync(input, id);
        await library.SaveSimpleAsync(input, id);
        Assert.Equal(oldIds, await db.GameOwnerships.Select(x => x.Id).OrderBy(x => x).ToListAsync());
        Assert.Single(await db.Stores.Where(x => x.Code == "unspecified").ToListAsync());
        Assert.Single(await db.Platforms.Where(x => x.Code == "playstation").ToListAsync());
        Assert.Single(await db.Games.ToListAsync());
    }

    [Fact]
    public async Task UnknownLegacyPlatformIsPreservedUnderOther()
    {
        var platform = new Platform { Code = "custom-console", Name = "Console legado", IgdbId = 654 };
        db.Platforms.Add(platform); await db.SaveChangesAsync();
        var id = await library.SaveAsync(new LibraryForm
        {
            Title = "Legado", Ownerships = [new() { StoreId = 7, PlatformId = platform.Id }]
        });
        await library.SaveSimpleAsync(Form("other"), id);
        var ownership = await db.GameOwnerships.Include(x => x.Platform).SingleAsync();
        Assert.Equal(platform.Id, ownership.PlatformId);
        Assert.Equal(7, ownership.StoreId);
        Assert.Equal(654, ownership.Platform.IgdbId);
        Assert.False(await db.Platforms.AnyAsync(x => x.Code == "other"));
        Assert.False(await db.Stores.AnyAsync(x => x.Code == "unspecified"));
    }

    [Fact]
    public async Task ClearsAbandonmentReasonWithoutTouchingPossessions()
    {
        var input = Form("pc"); input.Status = LibraryStatus.Abandoned; input.AbandonmentReason = "Motivo";
        var id = await library.SaveSimpleAsync(input);
        var ownershipId = (await db.GameOwnerships.SingleAsync()).Id;
        input.Status = LibraryStatus.Playing;
        await library.SaveSimpleAsync(input, id);
        Assert.Null((await db.LibraryEntries.FindAsync(id))!.AbandonmentReason);
        Assert.Equal(ownershipId, (await db.GameOwnerships.SingleAsync()).Id);
    }

    [Fact]
    public async Task FailureRollsBackGenericReferencesAndNewGame()
    {
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOwnership BEFORE INSERT ON GameOwnerships BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => library.SaveSimpleAsync(Form("xbox", "playstation")));
        Assert.Empty(await db.Games.ToListAsync());
        Assert.Empty(await db.LibraryEntries.ToListAsync());
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Fact]
    public async Task FailedEditPreservesLegacyDataAndDoesNotLeaveNewReferences()
    {
        var id = await library.SaveAsync(new LibraryForm
        {
            Title = "Legado", EditionTitle = "Original", Rating = 1,
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }]
        });
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectOwnership BEFORE INSERT ON GameOwnerships BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        var input = Form("pc", "playstation"); input.Rating = 10;
        await Assert.ThrowsAsync<DbUpdateException>(() => library.SaveSimpleAsync(input, id));
        Assert.Equal(1, (await db.LibraryEntries.FindAsync(id))!.Rating);
        Assert.Equal(1, (await db.GameOwnerships.SingleAsync()).StoreId);
        Assert.Equal("Original", (await db.Games.SingleAsync()).EditionTitle);
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Fact]
    public async Task EqualTitlesAreNotAutomaticallyMerged()
    {
        await library.SaveSimpleAsync(Form("pc"));
        await library.SaveSimpleAsync(Form("pc"));
        Assert.Equal(2, await db.Games.CountAsync());
        Assert.Equal(2, await db.LibraryEntries.CountAsync());
    }

    [Theory]
    [InlineData("pc", 2)]
    [InlineData("playstation", 2)]
    [InlineData("other", 1)]
    public async Task ConfirmedRemovalDeletesOnlyTargetGroupAndPreservesOtherGamesAndSharedData(string removedGroup, int count)
    {
        var unknown = new Platform { Code = "legacy-console", Name = "Console legado", IgdbId = 456 };
        db.Platforms.Add(unknown); await db.SaveChangesAsync();
        var id = await library.SaveAsync(new LibraryForm
        {
            Title = "Original", EditionTitle = "Deluxe", Status = LibraryStatus.Playing, Rating = 7, Notes = "Notas",
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 2, PlatformId = 1 },
                new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 7, PlatformId = 3 },
                new() { StoreId = 7, PlatformId = unknown.Id }]
        });
        var otherId = await library.SaveAsync(new LibraryForm
        {
            Title = "Outro jogo", Rating = 10, Notes = "Não alterar",
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 4, PlatformId = 3 }]
        });
        var gameId = (await db.LibraryEntries.FindAsync(id))!.GameId;
        db.UserProfiles.Add(new() { Id = 2, DisplayName = "Outro perfil" });
        var otherProfile = new LibraryEntry
        {
            UserProfileId = 2, GameId = gameId, Rating = 5,
            Ownerships = [new() { StoreId = 4, PlatformId = 3 }]
        };
        db.LibraryEntries.Add(otherProfile); await db.SaveChangesAsync();
        // Verify that an unchanged legacy text is not silently normalized on an ownership-only edit.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE LibraryEntries SET Notes = '  Notas antigas  ' WHERE Id = {id}");
        db.ChangeTracker.Clear();
        var original = await (await library.QueryAsync()).SingleAsync(x => x.Id == id);
        var oldRows = original.Ownerships.ToList();
        var otherRows = await db.GameOwnerships.Where(x => x.LibraryEntryId != id).AsNoTracking().ToListAsync();
        var added = original.AddedAtUtc;
        var kept = new[] { "pc", "playstation", "other" }.Where(x => x != removedGroup).ToArray();
        var input = Form(kept);
        input.Status = original.Status; input.Rating = original.Rating; input.Notes = original.Notes;
        input.ConfirmedPlatformRemovals = [removedGroup];
        await library.SaveSimpleAsync(input, id);
        db.ChangeTracker.Clear();
        var after = await (await library.QueryAsync()).SingleAsync(x => x.Id == id);
        Assert.Equal(oldRows.Count - count, after.Ownerships.Count);
        foreach (var row in oldRows.Where(x => PlatformGroups.FromPlatformCode(x.Platform.Code) != removedGroup))
            Assert.Contains(after.Ownerships, x => x.Id == row.Id && x.StoreId == row.StoreId && x.PlatformId == row.PlatformId);
        Assert.DoesNotContain(after.Ownerships, x => PlatformGroups.FromPlatformCode(x.Platform.Code) == removedGroup);
        Assert.Equal(gameId, after.GameId);
        Assert.Equal("Deluxe", after.Game.EditionTitle);
        Assert.Equal(LibraryStatus.Playing, after.Status);
        Assert.Equal(7, after.Rating);
        Assert.Equal("  Notas antigas  ", after.Notes);
        Assert.Equal(added, after.AddedAtUtc);
        Assert.Equal(2, await db.Games.CountAsync());
        Assert.Equal(3, await db.LibraryEntries.CountAsync());
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(7, await db.Platforms.CountAsync());
        Assert.Equal(10, (await db.LibraryEntries.FindAsync(otherId))!.Rating);
        Assert.Equal("Não alterar", (await db.LibraryEntries.FindAsync(otherId))!.Notes);
        foreach (var old in otherRows)
            Assert.True(await db.GameOwnerships.AnyAsync(x => x.Id == old.Id && x.LibraryEntryId == old.LibraryEntryId && x.StoreId == old.StoreId && x.PlatformId == old.PlatformId));
    }

    [Theory]
    [InlineData("empty")] [InlineData("unknown")] [InlineData("duplicate")]
    [InlineData("unconfirmed")] [InlineData("extra-confirmation")]
    [InlineData("invalid-confirmation")] [InlineData("duplicate-confirmation")]
    public async Task InvalidRemovalDoesNotPersistAnything(string error)
    {
        var id = await library.SaveSimpleAsync(Form("pc", "playstation"));
        var ids = await db.GameOwnerships.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync();
        var input = Form("pc"); input.Rating = 10; input.ConfirmedPlatformRemovals = ["playstation"];
        switch (error)
        {
            case "empty": input.SelectedPlatforms.Clear(); input.ConfirmedPlatformRemovals.Add("pc"); break;
            case "unknown": input.SelectedPlatforms = ["invalid"]; break;
            case "duplicate": input.SelectedPlatforms = ["pc", "pc"]; break;
            case "unconfirmed": input.ConfirmedPlatformRemovals.Clear(); break;
            case "extra-confirmation": input.ConfirmedPlatformRemovals.Add("pc"); break;
            case "invalid-confirmation": input.ConfirmedPlatformRemovals = ["invalid"]; break;
            case "duplicate-confirmation": input.ConfirmedPlatformRemovals.Add("playstation"); break;
        }
        await Assert.ThrowsAsync<ValidationException>(() => library.SaveSimpleAsync(input, id));
        Assert.Equal(ids, await db.GameOwnerships.OrderBy(x => x.Id).Select(x => x.Id).ToListAsync());
        Assert.Null((await db.LibraryEntries.FindAsync(id))!.Rating);
    }

    [Fact]
    public async Task DatabaseFailureRollsBackRemovalAdditionAndPersonalChangesTogether()
    {
        var id = await library.SaveAsync(new LibraryForm
        {
            Title = "Legado", Status = LibraryStatus.Playing, Rating = 1, Notes = "Manter",
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }, new() { StoreId = 4, PlatformId = 2 }, new() { StoreId = 7, PlatformId = 3 }]
        });
        var oldIds = await db.GameOwnerships.Select(x => x.Id).OrderBy(x => x).ToListAsync();
        await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER RejectRemoval BEFORE DELETE ON GameOwnerships BEGIN SELECT RAISE(ABORT, 'test failure'); END;");
        var input = Form("pc", "nintendo"); input.Status = LibraryStatus.Completed; input.Rating = 10; input.Notes = "Não aplicar";
        input.ConfirmedPlatformRemovals = ["playstation"];
        await Assert.ThrowsAsync<DbUpdateException>(() => library.SaveSimpleAsync(input, id));
        Assert.Equal(oldIds, await db.GameOwnerships.Select(x => x.Id).OrderBy(x => x).ToListAsync());
        var entry = await db.LibraryEntries.FindAsync(id);
        Assert.Equal(LibraryStatus.Playing, entry!.Status);
        Assert.Equal(1, entry.Rating);
        Assert.Equal("Manter", entry.Notes);
        Assert.Equal(7, await db.Stores.CountAsync());
        Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Theory]
    [InlineData("pc", "pc")] [InlineData("ps4", "playstation")] [InlineData("ps5", "playstation")]
    [InlineData("switch", "nintendo")] [InlineData("xbox-one", "xbox")] [InlineData("xbox-series", "xbox")]
    [InlineData("unknown", "other")]
    public void MapsExistingPlatformCodesDeterministically(string code, string group) =>
        Assert.Equal(group, PlatformGroups.FromPlatformCode(code));

    public void Dispose() { db.Dispose(); connection.Dispose(); }
}
