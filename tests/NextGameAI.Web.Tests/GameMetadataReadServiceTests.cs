using System.Data.Common;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameMetadataReadServiceTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
    private sealed class Clock : TimeProvider
    { public override DateTimeOffset GetUtcNow() => new(Now); }

    private sealed class ReadAudit : DbCommandInterceptor
    {
        public readonly List<string> Commands = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { Commands.Add(command.CommandText); return ValueTask.FromResult(result); }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly SqliteConnection connection = new(TestDatabaseGuard.ConnectionString);
        public readonly ReadAudit Audit = new();
        public readonly NextGameAiDbContext Db;
        public readonly GameMetadataReadService Service;
        public Fixture()
        {
            var guard = new TestDatabaseGuard(connection); guard.Validate(connection); connection.Open();
            Db = new(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection)
                .AddInterceptors(guard, Audit).Options);
            Db.Database.Migrate();
            Service = new(Db, new GameEnrichmentPolicy(new Clock()));
        }
        public async Task<int> Game()
        {
            var game = new Game { Title = "Synthetic game" }; Db.Games.Add(game);
            await Db.SaveChangesAsync(); return game.Id;
        }
        public async Task Link(int id, CatalogTaxonomyKind kind, long external, string name)
        {
            Db.GameTaxonomies.Add(new() { GameId = id,
                CatalogTaxonomy = new() { Kind = kind, IgdbId = external, Name = name } });
            await Db.SaveChangesAsync();
        }
        public void Ready() { Db.ChangeTracker.Clear(); Audit.Commands.Clear(); }
        public void Dispose() { Db.Dispose(); connection.Dispose(); }
    }

    [Fact]
    public async Task MissingGameIsDistinctFromExistingGameWithoutMetadata()
    {
        using var f = new Fixture(); var id = await f.Game(); f.Ready();
        Assert.Null(await f.Service.GetByGameIdAsync(id + 1));
        var result = Assert.IsType<GameMetadataDetails>(await f.Service.GetByGameIdAsync(id));
        Assert.Empty(result.Genres); Assert.Empty(result.Themes); Assert.Empty(result.GameModes);
        Assert.Empty(result.PlayerPerspectives); Assert.Null(result.TimeToBeat); Assert.Empty(result.MultiplayerModes);
        Assert.Equal(6, result.States.Count);
        Assert.All(result.States, s => { Assert.Null(s.LastResult); Assert.Null(s.LastAttemptAtUtc);
            Assert.Null(s.LastSuccessAtUtc); Assert.Equal(EnrichmentFreshness.NeverQueried, s.Freshness); });
    }

    [Theory]
    [InlineData(CatalogTaxonomyKind.Genre)] [InlineData(CatalogTaxonomyKind.Theme)]
    [InlineData(CatalogTaxonomyKind.GameMode)] [InlineData(CatalogTaxonomyKind.PlayerPerspective)]
    public async Task FourAllowedTaxonomiesAreProjectedOrderedAndFilteredByGame(CatalogTaxonomyKind kind)
    {
        using var f = new Fixture(); var id = await f.Game(); var other = await f.Game();
        await f.Link(id, kind, 3, "Zulu"); await f.Link(id, kind, 2, "Alpha"); await f.Link(id, kind, 1, "Alpha");
        await f.Link(other, kind, 99, "Other game"); f.Ready();
        var result = (await f.Service.GetByGameIdAsync(id))!;
        var values = kind switch { CatalogTaxonomyKind.Genre => result.Genres, CatalogTaxonomyKind.Theme => result.Themes,
            CatalogTaxonomyKind.GameMode => result.GameModes, _ => result.PlayerPerspectives };
        Assert.Equal(new long[] { 1, 2, 3 }, values.Select(v => v.IgdbId));
        Assert.Equal(3, result.Genres.Count + result.Themes.Count + result.GameModes.Count + result.PlayerPerspectives.Count);
        Assert.Empty(f.Db.ChangeTracker.Entries()); Assert.Equal(5, f.Audit.Commands.Count);
    }

    [Fact]
    public async Task KeywordsAndTheirStateAreExcludedInSqlAndPublicSnapshotButRemainStored()
    {
        using var f = new Fixture(); var id = await f.Game();
        await f.Link(id, CatalogTaxonomyKind.Keyword, 100, "SPOILER_SYNTHETIC");
        f.Db.GameEnrichmentStates.Add(new() { GameId = id, Section = CatalogMetadataSection.Keywords,
            LastResult = CatalogUpdateResult.Success, LastAttemptAtUtc = Now, LastSuccessAtUtc = Now, Source = "PRIVATE_SOURCE" });
        await f.Db.SaveChangesAsync(); f.Ready();
        var result = (await f.Service.GetByGameIdAsync(id))!;
        var json = JsonSerializer.Serialize(result);
        Assert.DoesNotContain("SPOILER_SYNTHETIC", json); Assert.DoesNotContain("PRIVATE_SOURCE", json);
        Assert.DoesNotContain(result.States, s => s.Section == CatalogMetadataSection.Keywords);
        var sql = Assert.Single(f.Audit.Commands, c => c.Contains("GameTaxonomies"));
        Assert.Contains(" IN (0, 1, 2, 3)", sql);
        Assert.Empty(f.Db.ChangeTracker.Entries());
        Assert.Single(await f.Db.CatalogTaxonomies.ToListAsync()); Assert.Single(await f.Db.GameEnrichmentStates.ToListAsync());
    }

    [Theory]
    [InlineData(null, null, null)] [InlineData(0L, null, 0L)] [InlineData(600L, 3600L, 7200L)]
    public async Task DurationPreservesSecondsNullsZeroCountAndSource(long? hastily, long? normally, long? completely)
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Db.GameTimeToBeats.Add(new() { GameId = id, IgdbId = 10, HastilySeconds = hastily,
            NormallySeconds = normally, CompletelySeconds = completely, SubmissionCount = 0, Source = "IGDB" });
        await f.Db.SaveChangesAsync(); f.Ready();
        var time = (await f.Service.GetByGameIdAsync(id))!.TimeToBeat!;
        Assert.Equal(hastily, time.HastilySeconds); Assert.Equal(normally, time.NormallySeconds);
        Assert.Equal(completely, time.CompletelySeconds); Assert.Equal(0, time.SubmissionCount); Assert.Equal("IGDB", time.Source);
    }

    [Fact]
    public async Task MultiplayerPreservesExactExternalPlatformsUnknownPlatformAndNullableValues()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Db.GameMultiplayerModes.AddRange(
            new() { GameId = id, IgdbId = 11, CatalogPlatform = new() { IgdbId = 999, Name = "External console" },
                CampaignCoop = true, DropIn = false, LanCoop = null, OfflineCoop = false, OnlineCoop = true,
                SplitScreen = null, SplitScreenOnline = false, OfflineCoopMax = 0, OfflineMax = null,
                OnlineCoopMax = 4, OnlineMax = 8 },
            new() { GameId = id, IgdbId = 12 });
        await f.Db.SaveChangesAsync(); f.Ready();
        var rows = (await f.Service.GetByGameIdAsync(id))!.MultiplayerModes;
        Assert.Equal(2, rows.Count); Assert.Null(rows[0].Platform);
        var row = rows[1]; Assert.Equal(999, row.Platform!.IgdbId); Assert.Equal("External console", row.Platform.Name);
        Assert.True(row.CampaignCoop); Assert.False(row.DropIn); Assert.Null(row.LanCoop);
        Assert.False(row.OfflineCoop); Assert.True(row.OnlineCoop); Assert.Null(row.SplitScreen); Assert.False(row.SplitScreenOnline);
        Assert.Equal(0, row.OfflineCoopMax); Assert.Null(row.OfflineMax); Assert.Equal(4, row.OnlineCoopMax); Assert.Equal(8, row.OnlineMax);
        Assert.Null(rows[0].CampaignCoop); Assert.Null(rows[0].OnlineMax);
        Assert.Empty(f.Db.ChangeTracker.Entries());
    }

    [Theory]
    [InlineData(CatalogUpdateResult.Success, 0, EnrichmentFreshness.Recent)]
    [InlineData(CatalogUpdateResult.Success, 30, EnrichmentFreshness.Expired)]
    [InlineData(CatalogUpdateResult.Success, 31, EnrichmentFreshness.Expired)]
    [InlineData(CatalogUpdateResult.Success, -1, EnrichmentFreshness.InconsistentTimestamp)]
    [InlineData(CatalogUpdateResult.TemporaryFailure, 3, EnrichmentFreshness.Recent)]
    [InlineData(CatalogUpdateResult.NotFound, 31, EnrichmentFreshness.Expired)]
    public async Task PersistedResultAndPerSectionDatesUseExistingFreshnessWithoutHidingContent(
        CatalogUpdateResult lastResult, int days, EnrichmentFreshness expected)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Link(id, CatalogTaxonomyKind.Genre, 1, "Genre");
        var success = Now.AddDays(-days); var attempt = lastResult == CatalogUpdateResult.Success ? success : Now;
        f.Db.GameEnrichmentStates.Add(new() { GameId = id, Section = CatalogMetadataSection.Genres,
            LastResult = lastResult, LastAttemptAtUtc = attempt, LastSuccessAtUtc = success });
        await f.Db.SaveChangesAsync(); f.Ready();
        var result = (await f.Service.GetByGameIdAsync(id))!;
        var state = Assert.Single(result.States, s => s.Section == CatalogMetadataSection.Genres);
        Assert.Equal(lastResult, state.LastResult); Assert.Equal(expected, state.Freshness);
        Assert.Equal(attempt, state.LastAttemptAtUtc); Assert.Equal(success, state.LastSuccessAtUtc);
        Assert.Equal(DateTimeKind.Utc, state.LastAttemptAtUtc!.Value.Kind); Assert.Single(result.Genres);
        Assert.Empty(result.Themes); Assert.Null(result.TimeToBeat);
    }

    [Theory]
    [InlineData(CatalogUpdateResult.TemporaryFailure)] [InlineData(CatalogUpdateResult.NotFound)]
    public async Task AttemptWithoutSuccessDoesNotInventConfirmedUpdate(CatalogUpdateResult lastResult)
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Db.GameEnrichmentStates.Add(new() { GameId = id, Section = CatalogMetadataSection.TimeToBeat,
            LastResult = lastResult, LastAttemptAtUtc = Now });
        await f.Db.SaveChangesAsync(); f.Ready();
        var state = (await f.Service.GetByGameIdAsync(id))!.States.Single(s => s.Section == CatalogMetadataSection.TimeToBeat);
        Assert.Equal(lastResult, state.LastResult); Assert.Null(state.LastSuccessAtUtc); Assert.Null(state.ExpiresAtUtc);
        Assert.Equal(EnrichmentFreshness.NoSuccessfulUpdate, state.Freshness);
    }

    [Fact]
    public async Task ReadHasNoHttpDependenciesNoWritesAndDoesNotSavePendingPersonalChanges()
    {
        using var f = new Fixture(); var id = await f.Game();
        var entry = new LibraryEntry { GameId = id, UserProfileId = LocalProfile.Id, Rating = 0,
            Status = LibraryStatus.Playing, Notes = "Personal", AddedAtUtc = Now, UpdatedAtUtc = Now,
            Ownerships = [new() { StoreId = 1, PlatformId = 1 }] };
        f.Db.LibraryEntries.Add(entry); await f.Db.SaveChangesAsync(); f.Ready();
        async Task<string> PersonalSnapshot() => JsonSerializer.Serialize(await f.Db.LibraryEntries.AsNoTracking()
            .Select(e => new { e.Id, e.UserProfileId, e.GameId, e.Status, e.Rating, e.Notes, e.AbandonmentReason,
                e.AddedAtUtc, e.UpdatedAtUtc, Possessions = e.Ownerships.OrderBy(o => o.Id)
                    .Select(o => new { o.Id, o.StoreId, o.PlatformId }).ToArray() }).ToArrayAsync());
        var before = await PersonalSnapshot();
        var tracked = await f.Db.LibraryEntries.SingleAsync(); tracked.Notes = "Unsaved personal change";
        f.Db.Games.Add(new() { Title = "Unsaved game" }); f.Audit.Commands.Clear();
        await f.Service.GetByGameIdAsync(id);
        Assert.Equal(5, f.Audit.Commands.Count); Assert.All(f.Audit.Commands, c => Assert.StartsWith("SELECT", c));
        Assert.Equal(2, f.Db.ChangeTracker.Entries().Count());
        Assert.Equal(before, await PersonalSnapshot());
        Assert.Equal(1, await f.Db.Games.CountAsync());
        Assert.Equal(new[] { typeof(NextGameAiDbContext), typeof(GameEnrichmentPolicy) },
            typeof(GameMetadataReadService).GetConstructors().Single().GetParameters().Select(p => p.ParameterType));
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)]
    public async Task InvalidIdentifiersDoNotQuery(int id)
    {
        using var f = new Fixture(); f.Ready();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => f.Service.GetByGameIdAsync(id)); Assert.Empty(f.Audit.Commands);
    }

    [Fact]
    public async Task CancellationBeforeReadDoesNotQuery()
    {
        using var f = new Fixture(); f.Ready(); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Service.GetByGameIdAsync(1, cancellation.Token));
        Assert.Empty(f.Audit.Commands);
    }

    [Fact]
    public void RegistrationIsScopedAndDoesNotRequireHttpForReading()
    {
        var services = new ServiceCollection(); services.AddIgdbSearch();
        var descriptor = Assert.Single(services, s => s.ServiceType == typeof(GameMetadataReadService));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }
}
