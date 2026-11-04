using System.Data;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Data;
using NextGameAI.Web.Models;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameEnrichmentDetailsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 2, 12, 0, 0, TimeSpan.FromHours(-3));
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private sealed class Metadata : IIgdbMetadataClient
    {
        public Func<long, IgdbMetadataResult> Response = Good;
        public Action? Before;
        public int Calls;
        public Task<IgdbMetadataResult> GetByIdAsync(long id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Coordinated enrichment must use selective queries.");
        public Task<IgdbMetadataResult> GetSectionsAsync(long id, IReadOnlyCollection<IgdbMetadataSectionKind> sections, CancellationToken ct = default)
        { Calls++; ct.ThrowIfCancellationRequested(); Before?.Invoke(); return Task.FromResult(Response(id)); }
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
            services.AddSingleton<GameEnrichmentCoordinator>(); services.AddSingleton<GameEnrichmentPolicy>();
            services.AddSingleton<GameEnrichmentCooldown>();
            services.AddScoped<IGameEnrichmentService, GameEnrichmentService>();
            Root = services.BuildServiceProvider(validateScopes: true);
            Service = new GameEnrichmentService(Root.GetRequiredService<IServiceScopeFactory>(), Metadata, Clock,
                Root.GetRequiredService<GameEnrichmentCoordinator>(), Root.GetRequiredService<GameEnrichmentPolicy>(), Root.GetRequiredService<GameEnrichmentCooldown>());
            using var db = Db(); db.Database.Migrate();
        }
        public NextGameAiDbContext Db() => new(new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(Connection).AddInterceptors(guard).Options);
        public async Task<int> Game(long? externalId = 974)
        {
            using var db = Db(); var game = new Game { IgdbId = externalId, Title = "Synthetic game" };
            db.Games.Add(game); await db.SaveChangesAsync(); return game.Id;
        }
        public async Task<string> Snapshot(params string[] tables)
        {
            var result = new List<object>();
            foreach (var table in tables)
            {
                using var command = Connection.CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                using var reader = await command.ExecuteReaderAsync(); var rows = new List<object[]>();
                while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
                result.Add(rows);
            }
            return JsonSerializer.Serialize(result);
        }
        public void Dispose() { Root.Dispose(); Connection.Dispose(); }
    }
    private static IgdbMultiplayerResult Mode(long game, long? platform = 6, long? record = null) => new(record ?? game * 10 + (platform ?? 0), game,
        platform.HasValue ? new(platform.Value, "Synthetic platform " + platform.Value) : null,
        true, false, null, false, true, null, true, 0, null, 4, null);
    private static IgdbMetadataSection<IReadOnlyList<IgdbMultiplayerResult>> Multi(params IgdbMultiplayerResult[] rows) =>
        new(IgdbMetadataStatus.Success, Array.AsReadOnly(rows));
    [Fact]
    public async Task DetailHttpDiagnosticsArePreservedWithoutReplacingPreviousMetadata()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms");
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = external => Good(external) with
        {
            TimeToBeat = new(IgdbMetadataStatus.TransportFailure, Failure: IgdbQueryFailure.RateLimited,
                StatusCode: System.Net.HttpStatusCode.TooManyRequests, RetryAfter: TimeSpan.FromHours(2)),
            Multiplayer = new(IgdbMetadataStatus.TransportFailure, Failure: IgdbQueryFailure.HttpError,
                StatusCode: System.Net.HttpStatusCode.ServiceUnavailable)
        };
        var result = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(TimeSpan.FromHours(2), result.Sections.Single(s => s.Section == CatalogMetadataSection.TimeToBeat).RetryAfter);
        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, result.Sections.Single(s => s.Section == CatalogMetadataSection.Multiplayer).StatusCode);
        Assert.Equal(before, await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms"));
    }

    private static IgdbMetadataResult Good(long game)
    {
        IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Tax(IgdbTaxonomyKind kind) =>
            new(IgdbMetadataStatus.Success, Array.AsReadOnly(new[] { new IgdbTaxonomyResult(kind, 10, "Synthetic reference") }));
        return new(game, Tax(IgdbTaxonomyKind.Genre), Tax(IgdbTaxonomyKind.Theme), Tax(IgdbTaxonomyKind.GameMode),
            Tax(IgdbTaxonomyKind.PlayerPerspective), Tax(IgdbTaxonomyKind.Keyword),
            new(IgdbMetadataStatus.Success, new(game, game, 0, 72669, null, 0)), Multi(Mode(game)));
    }

    [Fact]
    public async Task CreatesDurationAndSevenBooleansFourNullableLimitsOnCorrectExternalPlatform()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Before = () => { using var cmd = f.Connection.CreateCommand(); cmd.CommandText = "BEGIN; ROLLBACK;"; cmd.ExecuteNonQuery(); };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Applied, r.Outcome); Assert.Equal(2, r.Sections.Count);
        using var db = f.Db(); var time = await db.GameTimeToBeats.SingleAsync();
        Assert.Equal(id, time.GameId); Assert.Equal(974, time.IgdbId); Assert.Equal("IGDB", time.Source);
        Assert.Equal(0, time.HastilySeconds); Assert.Equal(72669, time.NormallySeconds);
        Assert.Null(time.CompletelySeconds); Assert.Equal(0, time.SubmissionCount);
        var mode = await db.GameMultiplayerModes.Include(m => m.CatalogPlatform).SingleAsync();
        Assert.Equal(id, mode.GameId); Assert.Equal(9746, mode.IgdbId); Assert.Equal(6, mode.CatalogPlatform!.IgdbId);
        Assert.True(mode.CampaignCoop); Assert.False(mode.DropIn); Assert.Null(mode.LanCoop);
        Assert.False(mode.OfflineCoop); Assert.True(mode.OnlineCoop); Assert.Null(mode.SplitScreen); Assert.True(mode.SplitScreenOnline);
        Assert.Equal(0, mode.OfflineCoopMax); Assert.Null(mode.OfflineMax); Assert.Equal(4, mode.OnlineCoopMax); Assert.Null(mode.OnlineMax);
        Assert.Equal("IGDB", mode.Source);
        var states = await db.GameEnrichmentStates.OrderBy(s => s.Section).ToArrayAsync();
        Assert.Equal(new[] { CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer }, states.Select(s => s.Section));
        Assert.All(states, s => { Assert.Equal(CatalogUpdateResult.Success, s.LastResult); Assert.Equal(f.Clock.Now.UtcDateTime, s.LastAttemptAtUtc); Assert.Equal(s.LastAttemptAtUtc, s.LastSuccessAtUtc); });
        Assert.Empty(await db.CatalogTaxonomies.ToArrayAsync());
    }

    [Fact]
    public async Task UpdatesNullableFieldsKeepsLocalIdsAndIsSequentiallyIdempotent()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        int modeId; using (var db = f.Db()) modeId = (await db.GameMultiplayerModes.SingleAsync()).Id;
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with
        {
            TimeToBeat = new(IgdbMetadataStatus.Success, new(500, game, null, null, 0, null)),
            Multiplayer = Multi(Mode(game) with { CampaignCoop = null, DropIn = true, OnlineCoop = false, OnlineCoopMax = null, OnlineMax = 0 })
        };
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms", "GameEnrichmentStates");
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(before, await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms", "GameEnrichmentStates"));
        using var read = f.Db(); var time = await read.GameTimeToBeats.SingleAsync();
        Assert.Equal(500, time.IgdbId); Assert.Null(time.HastilySeconds); Assert.Null(time.NormallySeconds);
        Assert.Equal(0, time.CompletelySeconds); Assert.Null(time.SubmissionCount);
        var mode = await read.GameMultiplayerModes.SingleAsync(); Assert.Equal(modeId, mode.Id);
        Assert.Null(mode.CampaignCoop); Assert.True(mode.DropIn); Assert.False(mode.OnlineCoop); Assert.Null(mode.OnlineCoopMax); Assert.Equal(0, mode.OnlineMax);
    }

    [Fact]
    public async Task ReusesAndRenamesSharedExternalPlatformWithoutTouchingOtherGameModes()
    {
        using var f = new Fixture(); var first = await f.Game(); var other = await f.Game(132181);
        await f.Service.EnrichDurationAndMultiplayerAsync(first); await f.Service.EnrichDurationAndMultiplayerAsync(other);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { Multiplayer = Multi(Mode(game) with { Platform = new(6, "Synthetic renamed") }, Mode(game, 48), Mode(game, 167)) };
        await f.Service.EnrichDurationAndMultiplayerAsync(first);
        using var db = f.Db(); Assert.Equal(3, await db.CatalogPlatforms.CountAsync());
        Assert.Equal("Synthetic renamed", (await db.CatalogPlatforms.SingleAsync(p => p.IgdbId == 6)).Name);
        Assert.Equal(3, await db.GameMultiplayerModes.CountAsync(m => m.GameId == first));
        Assert.Equal(1, await db.GameMultiplayerModes.CountAsync(m => m.GameId == other));
        Assert.Equal(2, await db.GameTimeToBeats.CountAsync());
    }

    [Fact]
    public async Task SingleUnknownPlatformIsPreservedAndNeverAssignedToPersonalPlatform()
    {
        using var f = new Fixture(); var id = await f.Game(); f.Metadata.Response = game => Good(game) with { Multiplayer = Multi(Mode(game, null)) };
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(2, f.Metadata.Calls);
        using var db = f.Db(); Assert.Null((await db.GameMultiplayerModes.SingleAsync()).CatalogPlatformId);
        Assert.Empty(await db.CatalogPlatforms.ToListAsync()); Assert.Equal(6, await db.Platforms.CountAsync());
    }

    [Fact]
    public async Task NonemptyMultiplayerResponseNeverDeletesUnreturnedModesOrPlatforms()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Response = game => Good(game) with { Multiplayer = Multi(Mode(game, 6), Mode(game, 48)) };
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { Multiplayer = Multi(Mode(game, 6) with { OnlineMax = 8 }) };
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        using var db = f.Db(); Assert.Equal(2, await db.GameMultiplayerModes.CountAsync()); Assert.Equal(2, await db.CatalogPlatforms.CountAsync());
        Assert.Equal(8, (await db.GameMultiplayerModes.Include(m => m.CatalogPlatform).SingleAsync(m => m.CatalogPlatform!.IgdbId == 6)).OnlineMax);
    }

    [Theory]
    [InlineData("negative-hastily")] [InlineData("negative-normally")] [InlineData("negative-completely")]
    [InlineData("negative-count")] [InlineData("id")] [InlineData("game")] [InlineData("null")]
    [InlineData("failure")] [InlineData("empty")]
    public async Task InvalidOrEmptyTimePreservesPreviousDataAndLastSuccess(string invalid)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameTimeToBeats"); f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { TimeToBeat = invalid switch
        {
            "failure" => new(IgdbMetadataStatus.TransportFailure, Failure: IgdbQueryFailure.Timeout),
            "empty" => new(IgdbMetadataStatus.Empty),
            "null" => new(IgdbMetadataStatus.Success),
            _ => new(IgdbMetadataStatus.Success, new IgdbTimeToBeatResult(invalid == "id" ? 0 : game,
                invalid == "game" ? 132181 : game, invalid == "negative-hastily" ? -1 : 0,
                invalid == "negative-normally" ? -1 : 72669, invalid == "negative-completely" ? -1 : null,
                invalid == "negative-count" ? -1 : 0))
        } };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Partial, r.Outcome); Assert.Equal(before, await f.Snapshot("GameTimeToBeats"));
        using var db = f.Db(); var state = await db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.TimeToBeat);
        Assert.Equal(CatalogUpdateResult.TemporaryFailure, state.LastResult);
        Assert.Equal(f.Clock.Now.AddDays(-30).UtcDateTime, state.LastSuccessAtUtc); Assert.Equal(f.Clock.Now.UtcDateTime, state.LastAttemptAtUtc);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ExternalDurationRecordWithoutAnyEstimateNeverCreatesOrErasesAnEstimate(bool existing)
    {
        using var f = new Fixture(); var id = await f.Game();
        if (existing) await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameTimeToBeats");
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { TimeToBeat = new(IgdbMetadataStatus.Success, new(game, game, null, null, null, 5)) };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(TaxonomyEnrichmentOutcome.EmptyPreserved, r.Sections.Single(s => s.Section == CatalogMetadataSection.TimeToBeat).Outcome);
        Assert.Equal(before, await f.Snapshot("GameTimeToBeats"));
    }

    [Fact]
    public async Task EmptyInitialResponseCreatesNoFictionalDurationOrMode()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Response = game => Good(game) with { TimeToBeat = new(IgdbMetadataStatus.Empty), Multiplayer = new(IgdbMetadataStatus.Empty, Array.Empty<IgdbMultiplayerResult>()) };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Preserved, r.Outcome);
        Assert.All(r.Sections, s => Assert.Equal(TaxonomyEnrichmentOutcome.EmptyPreserved, s.Outcome));
        using var db = f.Db(); Assert.Empty(await db.GameTimeToBeats.ToListAsync()); Assert.Empty(await db.GameMultiplayerModes.ToListAsync());
        Assert.All(await db.GameEnrichmentStates.ToListAsync(), s => { Assert.Null(s.LastSuccessAtUtc); Assert.Equal(CatalogUpdateResult.TemporaryFailure, s.LastResult); });
    }

    [Theory]
    [InlineData("duplicate-record")] [InlineData("duplicate-platform")] [InlineData("unknowns")]
    [InlineData("invalid-platform")] [InlineData("unresolved")] [InlineData("long-name")]
    [InlineData("wrong-game")] [InlineData("id")] [InlineData("negative-offlinecoopmax")]
    [InlineData("negative-offlinemax")] [InlineData("negative-onlinecoopmax")] [InlineData("negative-onlinemax")]
    [InlineData("empty")] [InlineData("failure")] [InlineData("full-page")]
    public async Task InvalidMultiplayerRejectsEntireSectionWhileValidTimeCanUpdate(string invalid)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms");
        f.Metadata.Response = game =>
        {
            var row = Mode(game);
            var rows = invalid switch
            {
                "duplicate-record" => new[] { row, Mode(game, 48, row.IgdbId) },
                "duplicate-platform" => new[] { row, Mode(game, 6, row.IgdbId + 1) },
                "unknowns" => new[] { Mode(game, null, 1), Mode(game, null, 2) },
                "invalid-platform" => new[] { row with { Platform = new(0, "Synthetic") } },
                "unresolved" => new[] { row with { Platform = new(6, " ") } },
                "long-name" => new[] { row with { Platform = new(6, new string('x', 201)) } },
                "wrong-game" => new[] { row with { GameIgdbId = 132181 } },
                "id" => new[] { row with { IgdbId = 0 } },
                "negative-offlinecoopmax" => new[] { row with { OfflineCoopMax = -1 } },
                "negative-offlinemax" => new[] { row with { OfflineMax = -1 } },
                "negative-onlinecoopmax" => new[] { row with { OnlineCoopMax = -1 } },
                "negative-onlinemax" => new[] { row with { OnlineMax = -1 } },
                "full-page" => Enumerable.Range(1, 100).Select(i => Mode(game, i, i)).ToArray(),
                _ => new[] { row }
            };
            return Good(game) with
            {
                TimeToBeat = new(IgdbMetadataStatus.Success, new(game, game, 1, 2, 3, 4)),
                Multiplayer = invalid == "empty" ? new(IgdbMetadataStatus.Empty, Array.Empty<IgdbMultiplayerResult>())
                    : invalid == "failure" ? new(IgdbMetadataStatus.InvalidResponse) : Multi(rows)
            };
        };
        f.Clock.Now = f.Clock.Now.AddDays(30);
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Partial, r.Outcome); Assert.Equal(before, await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms"));
        using var db = f.Db(); Assert.Equal(2, (await db.GameTimeToBeats.SingleAsync()).NormallySeconds);
        var state = await db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Multiplayer);
        Assert.Equal(CatalogUpdateResult.TemporaryFailure, state.LastResult); Assert.Equal(f.Clock.Now.AddDays(-30).UtcDateTime, state.LastSuccessAtUtc);
    }

    [Theory]
    [InlineData("record-moved")] [InlineData("record-replaced")]
    public async Task ExistingRecordIdentityConflictPreservesEntireSectionBeforeCreatingNewPlatforms(string conflict)
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms");
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { Multiplayer = Multi(Mode(game, 167), conflict == "record-moved" ? Mode(game, 48, game * 10 + 6) : Mode(game, 6, 77)) };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(TaxonomyEnrichmentOutcome.InvalidResponse, r.Sections.Single(s => s.Section == CatalogMetadataSection.Multiplayer).Outcome);
        Assert.Equal(before, await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms"));
    }

    [Fact]
    public async Task ExternalRecordOwnedByAnotherGameCannotBeReassigned()
    {
        using var f = new Fixture(); var first = await f.Game(); var other = await f.Game(132181);
        await f.Service.EnrichDurationAndMultiplayerAsync(first);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { TimeToBeat = new(IgdbMetadataStatus.Success, new(974, game, 1, 2, 3, 4)), Multiplayer = Multi(Mode(game, 6, 9746)) };
        var before = await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms");
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(other); Assert.Equal(GameEnrichmentOutcome.Preserved, r.Outcome);
        Assert.All(r.Sections, s => Assert.Equal(TaxonomyEnrichmentOutcome.InvalidResponse, s.Outcome));
        Assert.Equal(before, await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms"));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task WriteFailureRollsBackNewPlatformsModeTimeAndStatesTogether(bool seed)
    {
        using var f = new Fixture(); var id = await f.Game(); if (seed) await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var before = await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms", "GameEnrichmentStates");
        using (var cmd = f.Connection.CreateCommand())
        {
            cmd.CommandText = "CREATE TRIGGER FailMode BEFORE INSERT ON GameMultiplayerModes BEGIN SELECT RAISE(ABORT,'synthetic failure'); END;"; cmd.ExecuteNonQuery();
        }
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { TimeToBeat = new(IgdbMetadataStatus.Success, new(game, game, 1, 2, 3, 4)), Multiplayer = Multi(Mode(game, 48)) };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id); Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, r.Outcome);
        Assert.Empty(r.Sections); Assert.Equal(before, await f.Snapshot("GameTimeToBeats", "GameMultiplayerModes", "CatalogPlatforms", "GameEnrichmentStates"));
    }

    [Fact]
    public async Task DetailsNeverModifyTaxonomiesPersonalRowsOrCallersPendingGame()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichTaxonomiesAsync(id);
        using (var db = f.Db())
        {
            db.LibraryEntries.Add(new() { GameId = id, UserProfileId = 1, Rating = 0, Notes = "Synthetic private", Ownerships = [new() { StoreId = 1, PlatformId = 1 }] });
            await db.SaveChangesAsync();
        }
        string[] tables = ["Games", "LibraryEntries", "GameOwnerships", "UserProfiles", "Stores", "Platforms", "CatalogTaxonomies", "GameTaxonomies"];
        var before = await f.Snapshot(tables);
        using var caller = f.Db(); var game = await caller.Games.SingleAsync(); game.Title = "Do not save";
        await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(before, await f.Snapshot(tables)); Assert.Equal(EntityState.Modified, caller.Entry(game).State);
        using var read = f.Db(); Assert.Equal(7, await read.GameEnrichmentStates.CountAsync());
    }

    [Theory]
    [InlineData("missing")] [InlineData("manual")] [InlineData("mismatch")]
    public async Task MissingManualOrDivergentGameCannotPersistDetails(string condition)
    {
        using var f = new Fixture(); var id = condition == "missing" ? 999 : await f.Game(condition == "manual" ? null : 974);
        if (condition == "mismatch") f.Metadata.Response = _ => Good(132181);
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(condition == "missing" ? GameEnrichmentOutcome.GameNotFound : condition == "manual" ? GameEnrichmentOutcome.MissingIgdbId : GameEnrichmentOutcome.IdentityMismatch, r.Outcome);
        using var db = f.Db(); Assert.Empty(await db.GameTimeToBeats.ToListAsync()); Assert.Empty(await db.GameMultiplayerModes.ToListAsync()); Assert.Empty(await db.GameEnrichmentStates.ToListAsync());
    }
    [Fact]
    public async Task NotRequestedMultiplayerKeepsContentAndStateWhileTimeUpdates()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service.EnrichDurationAndMultiplayerAsync(id);
        var previous = await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms");
        GameEnrichmentState previousState;
        using (var db = f.Db()) previousState = await db.GameEnrichmentStates.AsNoTracking().SingleAsync(s => s.Section == CatalogMetadataSection.Multiplayer);
        f.Clock.Now = f.Clock.Now.AddDays(30);
        f.Metadata.Response = game => Good(game) with { Multiplayer = new(IgdbMetadataStatus.NotRequested), TimeToBeat = new(IgdbMetadataStatus.Success, new(game, game, 1, 2, 3, 4)) };
        var r = await f.Service.EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(CatalogMetadataSection.TimeToBeat, Assert.Single(r.Sections).Section);
        Assert.Equal(previous, await f.Snapshot("GameMultiplayerModes", "CatalogPlatforms"));
        using var read = f.Db(); var stored = await read.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Multiplayer);
        Assert.Equal(previousState.LastAttemptAtUtc, stored.LastAttemptAtUtc); Assert.Equal(previousState.LastSuccessAtUtc, stored.LastSuccessAtUtc);
        Assert.Equal(2, (await read.GameTimeToBeats.SingleAsync()).NormallySeconds);
    }

}
