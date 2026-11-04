using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
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
public sealed partial class GameEnrichmentIntegrationTests
{
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static void WatchWriterWait(GameEnrichmentCoordinator coordinator, Action observer) =>
        typeof(GameEnrichmentCoordinator).GetProperty("WriterWaitRegistered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(coordinator, observer);
    private sealed class Clock : TimeProvider
    {
        private long ticks = new DateTime(2026, 11, 3, 12, 0, 0, DateTimeKind.Utc).Ticks;
        private readonly object timersGate = new();
        private readonly List<ManualTimer> timers = [];
        public Action? TimerScheduled;
        public bool FailScheduling;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan value)
        {
            Interlocked.Add(ref ticks, value.Ticks);
            ManualTimer[] due;
            lock (timersGate)
            {
                due = timers.Where(t => t.Due <= Interlocked.Read(ref ticks)).ToArray();
                foreach (var timer in due) timer.Due = long.MaxValue;
            }
            foreach (var timer in due) timer.Callback(timer.State);
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (FailScheduling) throw new InvalidOperationException("Synthetic scheduling failure");
            var timer = new ManualTimer(this, callback, state);
            timer.Change(dueTime, period); TimerScheduled?.Invoke(); return timer;
        }
        private sealed class ManualTimer(Clock owner, TimerCallback callback, object? state) : ITimer
        {
            public readonly TimerCallback Callback = callback;
            public readonly object? State = state;
            public long Due = long.MaxValue;
            private bool disposed;
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (period != Timeout.InfiniteTimeSpan) throw new InvalidOperationException("Only one-shot test timers are supported.");
                lock (owner.timersGate)
                {
                    if (disposed) return false;
                    Due = dueTime == Timeout.InfiniteTimeSpan ? long.MaxValue : Interlocked.Read(ref owner.ticks) + dueTime.Ticks;
                    if (!owner.timers.Contains(this)) owner.timers.Add(this);
                    return true;
                }
            }
            public void Dispose() { lock (owner.timersGate) { disposed = true; owner.timers.Remove(this); } }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
    private sealed record Query(long Id, IgdbMetadataSectionKind[] Sections);
    private sealed class Metadata : IIgdbMetadataClient
    {
        public readonly ConcurrentQueue<Query> Queries = new();
        public Func<long, IReadOnlyCollection<IgdbMetadataSectionKind>, CancellationToken, Task<IgdbMetadataResult>> Handler =
            (id, _, _) => Task.FromResult(Good(id));
        public Task<IgdbMetadataResult> GetByIdAsync(long id, CancellationToken ct = default) =>
            throw new InvalidOperationException("Complete query is forbidden in coordinated enrichment.");
        public Task<IgdbMetadataResult> GetSectionsAsync(long id, IReadOnlyCollection<IgdbMetadataSectionKind> sections, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); Queries.Enqueue(new(id, sections.ToArray())); return Handler(id, sections, ct); }
    }

    // Separate guard: the existing private-connection TestDatabaseGuard is deliberately untouched.
    private sealed class NamedMemoryGuard(string expected) : DbConnectionInterceptor
    {
        public void Validate(DbConnection connection)
        {
            if (connection is not SqliteConnection || new SqliteConnectionStringBuilder(connection.ConnectionString) is not
                { Mode: SqliteOpenMode.Memory, Cache: SqliteCacheMode.Shared, ForeignKeys: true, Pooling: false } options || options.DataSource != expected)
                throw new InvalidOperationException("Only this fixture's named in-memory database is allowed.");
        }
        public override InterceptionResult ConnectionOpening(DbConnection connection, ConnectionEventData data, InterceptionResult result)
        { Validate(connection); return result; }
        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(DbConnection connection, ConnectionEventData data,
            InterceptionResult result, CancellationToken ct = default)
        { Validate(connection); return ValueTask.FromResult(result); }
    }
    private sealed class SaveHook : SaveChangesInterceptor
    {
        public Func<DbContext, CancellationToken, Task>? BeforeSave;
        public Func<DbContext, CancellationToken, Task>? AfterSave;
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        { if (BeforeSave is not null) await BeforeSave(data.Context!, ct); return result; }
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData data, int result, CancellationToken ct = default)
        { if (AfterSave is not null) await AfterSave(data.Context!, ct); return result; }
    }
    private sealed class CommitHook : DbTransactionInterceptor
    {
        public Func<CancellationToken, Task>? BeforeCommit;
        public Action? AfterCommit;
        public Action? BeforeRollback;
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken ct = default)
        { if (BeforeCommit is not null) await BeforeCommit(ct); return result; }
        public override Task TransactionCommittedAsync(DbTransaction transaction, TransactionEndEventData data, CancellationToken ct = default)
        { AfterCommit?.Invoke(); return Task.CompletedTask; }
        public override ValueTask<InterceptionResult> TransactionRollingBackAsync(DbTransaction transaction,
            TransactionEventData data, InterceptionResult result, CancellationToken ct = default)
        { BeforeRollback?.Invoke(); return ValueTask.FromResult(result); }
    }
    private sealed class Publisher(GameEnrichmentCooldown cooldown) : IGameEnrichmentPublisher
    {
        public Action<EnrichmentCooldownObservation>? BeforePublish;
        public Action<EnrichmentCooldownObservation>? AfterPublish;
        public readonly ConcurrentQueue<EnrichmentCooldownObservation> Calls = new();
        public void Publish(EnrichmentCooldownObservation observation)
        { Calls.Enqueue(observation); BeforePublish?.Invoke(observation); cooldown.RecordConfirmed(observation); AfterPublish?.Invoke(observation); }
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string name = "enrichment-test-" + Guid.NewGuid().ToString("N");
        private readonly string connectionString;
        private readonly SqliteConnection keeper;
        private readonly NamedMemoryGuard guard;
        public readonly Clock Clock = new();
        public readonly Metadata Metadata = new();
        public readonly GameEnrichmentCoordinator Coordinator = new();
        public readonly GameEnrichmentPolicy Policy;
        public readonly GameEnrichmentCooldown Cooldown;
        public readonly Publisher Publication;
        public readonly SaveHook Save = new();
        public readonly CommitHook Commit = new();
        public Action? ScopeDisposed;
        private readonly ServiceProvider root;
        public Fixture()
        {
            connectionString = new SqliteConnectionStringBuilder
                { DataSource = name, Mode = SqliteOpenMode.Memory, Cache = SqliteCacheMode.Shared, ForeignKeys = true, Pooling = false, DefaultTimeout = 1 }.ToString();
            guard = new(name); keeper = new(connectionString); guard.Validate(keeper); keeper.Open();
            Policy = new(Clock); Cooldown = new(Clock, Policy); Publication = new(Cooldown);
            var services = new ServiceCollection();
            services.AddScoped(_ => new ScopeProbe(() => ScopeDisposed?.Invoke()));
            services.AddScoped<NextGameAiDbContext>(sp => { _ = sp.GetRequiredService<ScopeProbe>(); return Db(); });
            root = services.BuildServiceProvider(validateScopes: true);
            using var db = Db(); db.Database.Migrate(); // Test memory only; never Program.cs or personal database.
        }
        public NextGameAiDbContext Db()
        {
            var connection = new SqliteConnection(connectionString); guard.Validate(connection);
            var options = new DbContextOptionsBuilder<NextGameAiDbContext>().UseSqlite(connection, contextOwnsConnection: true)
                .AddInterceptors(guard, Save, Commit).Options;
            return new NextGameAiDbContext(options);
        }
        public IGameEnrichmentService Service() => new GameEnrichmentService(root.GetRequiredService<IServiceScopeFactory>(), Metadata, Clock, Coordinator, Policy, Cooldown, Publication);
        public async Task<int> Game(long externalId = 974)
        { using var db = Db(); var game = new Game { Title = "Synthetic", IgdbId = externalId }; db.Games.Add(game); await db.SaveChangesAsync(); return game.Id; }
        public async Task<string> Snapshot()
        {
            using var db = Db(); await db.Database.OpenConnectionAsync();
            var values = new List<object>();
            foreach (var table in new[] { "Games", "CatalogTaxonomies", "GameTaxonomies", "GameTimeToBeats", "CatalogPlatforms", "GameMultiplayerModes", "GameEnrichmentStates", "LibraryEntries", "GameOwnerships" })
            {
                using var command = db.Database.GetDbConnection().CreateCommand(); command.CommandText = $"SELECT * FROM {table} ORDER BY rowid";
                using var reader = await command.ExecuteReaderAsync(); var rows = new List<object[]>();
                while (await reader.ReadAsync()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); }
                values.Add(rows);
            }
            return System.Text.Json.JsonSerializer.Serialize(values);
        }
        public void Dispose() { root.Dispose(); keeper.Dispose(); }
    }
    private sealed class ScopeProbe(Action onDispose) : IDisposable
    { public void Dispose() => onDispose(); }
    private static IgdbMetadataResult Good(long id)
    {
        IgdbMetadataSection<IReadOnlyList<IgdbTaxonomyResult>> Tax(IgdbTaxonomyKind kind) =>
            new(IgdbMetadataStatus.Success, new[] { new IgdbTaxonomyResult(kind, 10, "Synthetic") });
        return new(id, Tax(IgdbTaxonomyKind.Genre), Tax(IgdbTaxonomyKind.Theme), Tax(IgdbTaxonomyKind.GameMode),
            Tax(IgdbTaxonomyKind.PlayerPerspective), Tax(IgdbTaxonomyKind.Keyword),
            new(IgdbMetadataStatus.Success, new(id, id, 0, 120, null, 0)),
            new(IgdbMetadataStatus.Success, new[] { new IgdbMultiplayerResult(id * 10, id, new(6, "Synthetic PC"),
                true, false, null, true, false, null, false, 0, null, 2, null) }));
    }
    private static Task<GameEnrichmentResult> Run(IGameEnrichmentService service, int game, bool details, CancellationToken ct = default) =>
        details ? service.EnrichDurationAndMultiplayerAsync(game, ct) : service.EnrichTaxonomiesAsync(game, ct);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task SecondSameGameWaitsReloadsAndMakesNoDuplicateQuery(bool details)
    {
        using var f = new Fixture(); var id = await f.Game(); var entered = Signal(); var release = Signal();
        f.Metadata.Handler = async (external, _, ct) =>
        { entered.SetResult(); await release.Task.WaitAsync(ct); return Good(external); };
        var first = Run(f.Service(), id, details); await entered.Task.WaitAsync(Deadline);
        var second = Run(f.Service(), id, details); Assert.False(second.IsCompleted);
        release.SetResult(); var results = await Task.WhenAll(first, second).WaitAsync(Deadline);
        Assert.Equal(GameEnrichmentOutcome.Applied, results[0].Outcome);
        Assert.All(results[1].Decisions, d => Assert.Equal(GameEnrichmentDisposition.Recent, d.Disposition));
        Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Theory]
    [InlineData(-1, false)] [InlineData(0, true)] [InlineData(1, true)]
    public async Task ExactThirtyDaysControlsBothPublicMethods(long ticks, bool queried)
    {
        using var f = new Fixture(); var id = await f.Game();
        await Run(f.Service(), id, false); await Run(f.Service(), id, true);
        f.Clock.Advance(TimeSpan.FromDays(30) + TimeSpan.FromTicks(ticks));
        var tax = await Run(f.Service(), id, false); var detail = await Run(f.Service(), id, true);
        Assert.Equal(queried ? 4 : 2, f.Metadata.Queries.Count);
        Assert.Equal(queried ? GameEnrichmentOutcome.Applied : GameEnrichmentOutcome.Preserved, tax.Outcome);
        Assert.Equal(queried ? GameEnrichmentOutcome.Applied : GameEnrichmentOutcome.Preserved, detail.Outcome);
    }

    [Theory]
    [InlineData(IgdbMetadataStatus.TransportFailure, 15)]
    [InlineData(IgdbMetadataStatus.InvalidResponse, 60)]
    [InlineData(IgdbMetadataStatus.Empty, 1440)]
    [InlineData(IgdbMetadataStatus.GameNotFound, 1440)]
    public async Task ConfirmedSectionFailureBlocksOnlyThatSectionUntilExactDeadline(IgdbMetadataStatus status, int minutes)
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Handler = (external, _, _) => Task.FromResult(Good(external) with
            { Genres = new(status, status == IgdbMetadataStatus.Empty ? Array.Empty<IgdbTaxonomyResult>() : null) });
        await f.Service().EnrichTaxonomiesAsync(id);
        var blocked = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Single(f.Metadata.Queries);
        Assert.Contains(blocked.Decisions, d => d.Section == CatalogMetadataSection.Genres && d.Disposition == GameEnrichmentDisposition.CooldownActive);
        f.Clock.Advance(TimeSpan.FromMinutes(minutes) - TimeSpan.FromTicks(1));
        await f.Service().EnrichTaxonomiesAsync(id); Assert.Single(f.Metadata.Queries);
        f.Clock.Advance(TimeSpan.FromTicks(1)); f.Metadata.Handler = (external, _, _) => Task.FromResult(Good(external));
        await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(new[] { IgdbMetadataSectionKind.Genres }, f.Metadata.Queries.Last().Sections);
        using var db = f.Db(); Assert.Equal(CatalogUpdateResult.Success, (await db.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres)).LastResult);
    }

    [Fact]
    public async Task MixedStatesProduceSelectiveQueryAndFutureTimestampIsBlocked()
    {
        using var f = new Fixture(); var id = await f.Game(); var now = f.Clock.GetUtcNow().UtcDateTime;
        using (var db = f.Db())
        {
            db.GameEnrichmentStates.AddRange(new GameEnrichmentState { GameId = id, Section = CatalogMetadataSection.Genres,
                LastAttemptAtUtc = now, LastSuccessAtUtc = now, LastResult = CatalogUpdateResult.Success },
                new GameEnrichmentState { GameId = id, Section = CatalogMetadataSection.Themes, LastAttemptAtUtc = now, LastResult = CatalogUpdateResult.TemporaryFailure },
                new GameEnrichmentState { GameId = id, Section = CatalogMetadataSection.GameModes,
                    LastAttemptAtUtc = now.AddTicks(1), LastSuccessAtUtc = now.AddTicks(1), LastResult = CatalogUpdateResult.Success });
            await db.SaveChangesAsync();
        }
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(new[] { IgdbMetadataSectionKind.PlayerPerspectives, IgdbMetadataSectionKind.Keywords }, Assert.Single(f.Metadata.Queries).Sections);
        Assert.Contains(result.Decisions, d => d.Disposition == GameEnrichmentDisposition.TemporalInconsistency);
        using var read = f.Db(); Assert.Equal(now, (await read.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres)).LastAttemptAtUtc);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RevalidationDetectsInsertedStateOrChangedResultWithSameTimestamp(bool existing)
    {
        using var f = new Fixture(); var id = await f.Game(); var at = f.Clock.GetUtcNow().UtcDateTime.AddDays(-2);
        if (existing)
        { using var db = f.Db(); db.GameEnrichmentStates.Add(new() { GameId = id, Section = CatalogMetadataSection.Genres, LastAttemptAtUtc = at, LastResult = CatalogUpdateResult.TemporaryFailure }); await db.SaveChangesAsync(); }
        f.Metadata.Handler = async (external, _, _) =>
        {
            using var db = f.Db(); var state = await db.GameEnrichmentStates.SingleOrDefaultAsync(s => s.Section == CatalogMetadataSection.Genres);
            if (state is null) { state = new() { GameId = id, Section = CatalogMetadataSection.Genres, LastAttemptAtUtc = at }; db.GameEnrichmentStates.Add(state); }
            state.LastResult = CatalogUpdateResult.NotFound; await db.SaveChangesAsync(); return Good(external);
        };
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Contains(result.Decisions, d => d.Section == CatalogMetadataSection.Genres && d.Disposition == GameEnrichmentDisposition.DiscardedAfterRevalidation);
        Assert.Equal(GameEnrichmentOutcome.Partial, result.Outcome);
        using var read = f.Db(); Assert.False(await read.GameTaxonomies.AnyAsync(t => t.CatalogTaxonomy.Kind == CatalogTaxonomyKind.Genre));
        Assert.Equal(CatalogUpdateResult.NotFound, (await read.GameEnrichmentStates.SingleAsync(s => s.Section == CatalogMetadataSection.Genres)).LastResult);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task MismatchedExternalOrChangedLocalIdentityNeverPublishes(bool local)
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Handler = async (external, _, _) =>
        {
            if (local) { using var db = f.Db(); (await db.Games.SingleAsync()).IgdbId = 132181; await db.SaveChangesAsync(); }
            return Good(local ? external : 132181);
        };
        Assert.Equal(GameEnrichmentOutcome.IdentityMismatch, (await f.Service().EnrichTaxonomiesAsync(id)).Outcome);
        using var read = f.Db(); Assert.Empty(await read.GameEnrichmentStates.ToArrayAsync()); Assert.Equal(0, f.Cooldown.Count);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task DifferentGamesQueryBeforeGlobalWriteAndShareReferencesWithoutDuplicates(bool details)
    {
        using var f = new Fixture(); var first = await f.Game(); var second = await f.Game(132181);
        var bothQueried = Signal(); var releaseHttp = Signal(); var saving = Signal(); var releaseSave = Signal();
        var writerWaiting = Signal(); WatchWriterWait(f.Coordinator, () => writerWaiting.TrySetResult());
        var queries = 0; var saves = 0;
        f.Metadata.Handler = async (external, _, ct) =>
        { if (Interlocked.Increment(ref queries) == 2) bothQueried.SetResult(); await releaseHttp.Task.WaitAsync(ct); return Good(external); };
        f.Save.BeforeSave = async (_, ct) =>
        { if (Interlocked.Increment(ref saves) == 1) { saving.SetResult(); await releaseSave.Task.WaitAsync(ct); } };
        var a = Run(f.Service(), first, details); var b = Run(f.Service(), second, details);
        await bothQueried.Task.WaitAsync(Deadline); releaseHttp.SetResult(); await saving.Task.WaitAsync(Deadline);
        await writerWaiting.Task.WaitAsync(Deadline); // The second operation has registered its global semaphore wait.
        Assert.Equal(1, Volatile.Read(ref saves)); releaseSave.SetResult();
        await Task.WhenAll(a, b).WaitAsync(Deadline); Assert.Equal(2, saves);
        using var db = f.Db();
        Assert.Equal(details ? 1 : 0, await db.CatalogPlatforms.CountAsync());
        Assert.Equal(details ? 0 : 5, await db.CatalogTaxonomies.CountAsync());
        Assert.Equal(details ? 4 : 10, await db.GameEnrichmentStates.CountAsync());
        Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task PostClientRetryDeadlineIsFrozenBeforeWaitingForWrite()
    {
        using var f = new Fixture(); var id = await f.Game();
        using var other = await f.Coordinator.AcquireGameAsync(999); var held = await other.AcquireWriteAsync();
        var called = Signal(); var started = f.Clock.GetUtcNow().UtcDateTime;
        f.Metadata.Handler = (external, _, _) =>
        {
            f.Clock.Advance(TimeSpan.FromMinutes(10)); called.SetResult();
            return Task.FromResult(Good(external) with { Genres = new(IgdbMetadataStatus.TransportFailure,
                Failure: IgdbQueryFailure.RateLimited, StatusCode: HttpStatusCode.TooManyRequests, RetryAfter: TimeSpan.FromMinutes(40)) });
        };
        var operation = f.Service().EnrichTaxonomiesAsync(id); await called.Task.WaitAsync(Deadline);
        Assert.False(operation.IsCompleted); f.Clock.Advance(TimeSpan.FromMinutes(5)); held.Dispose();
        var result = await operation.WaitAsync(Deadline);
        Assert.Equal(HttpStatusCode.TooManyRequests, result.Sections.Single(s => s.Section == CatalogMetadataSection.Genres).StatusCode);
        using var db = f.Db(); var state = await db.GameEnrichmentStates.AsNoTracking().SingleAsync(s => s.Section == CatalogMetadataSection.Genres);
        var decision = f.Cooldown.Evaluate(id, state.Section, state);
        Assert.Equal(started.AddMinutes(50), decision.NextAllowedAtUtc);
        Assert.Equal(started.AddMinutes(10), state.LastAttemptAtUtc);
        await f.Service().EnrichTaxonomiesAsync(id); Assert.Single(f.Metadata.Queries);
    }

    [Fact]
    public async Task PublicationOccursOnlyAfterCommitAndPostCommitCancellationDoesNotHideSuccess()
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource();
        f.Commit.BeforeCommit = _ => { Assert.Equal(0, f.Cooldown.Count); return Task.CompletedTask; };
        f.Commit.AfterCommit = ct.Cancel;
        var result = await f.Service().EnrichTaxonomiesAsync(id, ct.Token);
        Assert.Equal(GameEnrichmentOutcome.Applied, result.Outcome); Assert.Equal(5, f.Cooldown.Count);
        using var db = f.Db(); Assert.All(await db.GameEnrichmentStates.ToArrayAsync(), s => Assert.Equal(CatalogUpdateResult.Success, s.LastResult));
    }

    [Fact]
    public async Task WriteFailureRollsBackAndOnlyPersistenceCooldownIsPublished()
    {
        using var f = new Fixture(); var id = await f.Game();
        using (var db = f.Db()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER FailMode BEFORE INSERT ON GameMultiplayerModes BEGIN SELECT RAISE(ABORT,'synthetic'); END;");
        Assert.Equal(GameEnrichmentOutcome.PersistenceFailure, (await f.Service().EnrichDurationAndMultiplayerAsync(id)).Outcome);
        using (var db = f.Db()) { Assert.Empty(await db.GameTimeToBeats.ToArrayAsync()); Assert.Empty(await db.CatalogPlatforms.ToArrayAsync()); Assert.Empty(await db.GameEnrichmentStates.ToArrayAsync()); }
        var skip = await f.Service().EnrichDurationAndMultiplayerAsync(id);
        Assert.All(skip.Decisions, d => Assert.Equal(GameEnrichmentDisposition.CooldownActive, d.Disposition)); Assert.Single(f.Metadata.Queries);
        using (var db = f.Db()) await db.Database.ExecuteSqlRawAsync("DROP TRIGGER FailMode;");
        f.Clock.Advance(TimeSpan.FromMinutes(1)); Assert.Equal(GameEnrichmentOutcome.Applied, (await f.Service().EnrichDurationAndMultiplayerAsync(id)).Outcome);
    }

    [Theory]
    [InlineData("http")] [InlineData("writer")] [InlineData("transaction")]
    public async Task CancellationBeforeCommitPublishesNothingAndReleasesLeases(string phase)
    {
        using var f = new Fixture(); var id = await f.Game(); using var ct = new CancellationTokenSource(); var entered = Signal();
        GameEnrichmentCoordinator.GameLease? other = null;
        GameEnrichmentCoordinator.GameLease.WriteLease? held = null;
        try
        {
            if (phase == "writer") { other = await f.Coordinator.AcquireGameAsync(999); held = await other.AcquireWriteAsync(); }
            f.Metadata.Handler = async (external, _, token) =>
            { if (phase != "transaction") entered.SetResult(); if (phase == "http") await Signal().Task.WaitAsync(token); return Good(external); };
            if (phase == "transaction") f.Save.BeforeSave = (_, token) => { entered.SetResult(); ct.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
            var task = f.Service().EnrichTaxonomiesAsync(id, ct.Token); await entered.Task.WaitAsync(Deadline); ct.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task.WaitAsync(Deadline));
            Assert.Equal(0, f.Cooldown.Count);
            using var db = f.Db(); Assert.Empty(await db.GameEnrichmentStates.ToArrayAsync()); Assert.Empty(await db.CatalogTaxonomies.ToArrayAsync());
        }
        finally { held?.Dispose(); other?.Dispose(); }
        Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task CommitExceptionReturnsIndeterminateWithoutFalsePublicationOrRetry()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Commit.BeforeCommit = _ => throw new SqliteException("Synthetic commit failure", 5);
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.CommitIndeterminate, result.Outcome);
        Assert.Equal(0, f.Cooldown.Count); Assert.Single(f.Metadata.Queries);
        using var db = f.Db(); Assert.Empty(await db.GameEnrichmentStates.ToArrayAsync());
    }

    [Fact]
    public async Task NotRequestedFromClientNeverCreatesStateOrConfirmedObservation()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Handler = (external, _, _) => Task.FromResult(Good(external) with { Genres = new(IgdbMetadataStatus.NotRequested) });
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Contains(result.Decisions, d => d.Section == CatalogMetadataSection.Genres && d.Disposition == GameEnrichmentDisposition.NotRequested);
        Assert.Equal(GameEnrichmentOutcome.Partial, result.Outcome);
        using var db = f.Db(); Assert.False(await db.GameEnrichmentStates.AnyAsync(s => s.Section == CatalogMetadataSection.Genres));
        Assert.Equal(4, f.Cooldown.Count);
    }

    [Fact]
    public void NamedFixtureGuardRejectsFilesOtherNamesAndPrivateMemoryBeforeOpen()
    {
        var guard = new NamedMemoryGuard("test-only");
        using var file = new SqliteConnection("Data Source=never-open.db");
        using var other = new SqliteConnection("Data Source=other;Mode=Memory;Cache=Shared;Foreign Keys=True;Pooling=False");
        using var privateMemory = new SqliteConnection(TestDatabaseGuard.ConnectionString);
        Assert.Throws<InvalidOperationException>(() => guard.Validate(file));
        Assert.Throws<InvalidOperationException>(() => guard.Validate(other));
        Assert.Throws<InvalidOperationException>(() => guard.Validate(privateMemory));
        Assert.Equal(System.Data.ConnectionState.Closed, file.State);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task CancelledSameGameWaiterDoesNotQueryOrCancelOwner(bool details)
    {
        using var f = new Fixture(); var id = await f.Game(); var entered = Signal(); var release = Signal();
        f.Metadata.Handler = async (external, _, ct) => { entered.SetResult(); await release.Task.WaitAsync(ct); return Good(external); };
        var owner = Run(f.Service(), id, details); await entered.Task.WaitAsync(Deadline);
        using var cancel = new CancellationTokenSource();
        var waiter = Run(f.Service(), id, details, cancel.Token); Assert.False(waiter.IsCompleted);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);
        Assert.False(owner.IsCompleted); Assert.Single(f.Metadata.Queries); Assert.Equal(0, f.Cooldown.Count);
        release.SetResult(); Assert.Equal(GameEnrichmentOutcome.Applied, (await owner.WaitAsync(Deadline)).Outcome);
        Assert.Equal(0, f.Coordinator.RegisteredGameCount);
    }

    [Fact]
    public async Task AllSectionsChangedAfterHttpCauseNoSaveOrPublication()
    {
        using var f = new Fixture(); var id = await f.Game(); var now = f.Clock.GetUtcNow().UtcDateTime;
        f.Metadata.Handler = async (external, _, _) =>
        {
            using var db = f.Db();
            foreach (var section in new[] { CatalogMetadataSection.TimeToBeat, CatalogMetadataSection.Multiplayer })
                db.GameEnrichmentStates.Add(new() { GameId = id, Section = section, LastAttemptAtUtc = now, LastSuccessAtUtc = now, LastResult = CatalogUpdateResult.Success });
            await db.SaveChangesAsync();
            f.Save.BeforeSave = (_, _) => throw new InvalidOperationException("Discarded results must not be saved.");
            return Good(external);
        };
        var result = await f.Service().EnrichDurationAndMultiplayerAsync(id);
        Assert.Equal(GameEnrichmentOutcome.Preserved, result.Outcome);
        Assert.Equal(2, result.Decisions.Count); Assert.All(result.Decisions, d => Assert.Equal(GameEnrichmentDisposition.DiscardedAfterRevalidation, d.Disposition));
        Assert.Empty(result.Sections); Assert.Equal(0, f.Cooldown.Count);
        using var read = f.Db(); Assert.Empty(await read.GameTimeToBeats.ToArrayAsync()); Assert.Empty(await read.CatalogPlatforms.ToArrayAsync());
    }

    [Fact]
    public async Task CompletelyRecentOrBlockedRequestNeverAcquiresWriterOrChangesState()
    {
        using var f = new Fixture(); var id = await f.Game(); await f.Service().EnrichTaxonomiesAsync(id);
        using var other = await f.Coordinator.AcquireGameAsync(999); using var writer = await other.AcquireWriteAsync();
        var result = await f.Service().EnrichTaxonomiesAsync(id).WaitAsync(Deadline);
        Assert.Single(f.Metadata.Queries); Assert.Empty(result.Sections); Assert.Equal(5, result.Decisions.Count);
        Assert.All(result.Decisions, d => Assert.Equal(GameEnrichmentDisposition.Recent, d.Disposition));
    }

    [Fact]
    public async Task HttpCanAcquireUnrelatedWriteLeaseAndLeavesNoOpenReadTransaction()
    {
        using var f = new Fixture(); var id = await f.Game();
        f.Metadata.Handler = async (external, _, ct) =>
        {
            using var other = await f.Coordinator.AcquireGameAsync(999, ct);
            using var writer = await other.AcquireWriteAsync(ct).WaitAsync(Deadline);
            using var db = f.Db(); await using var transaction = await db.Database.BeginTransactionAsync(ct);
            Assert.True(await db.Games.AnyAsync(ct)); await transaction.RollbackAsync(ct);
            return Good(external);
        };
        Assert.Equal(GameEnrichmentOutcome.Applied, (await f.Service().EnrichTaxonomiesAsync(id)).Outcome);
    }

    [Fact]
    public async Task LastIdentityCheckRollsBackChangesMadeByATrigger()
    {
        using var f = new Fixture(); var id = await f.Game();
        using (var db = f.Db()) await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER ChangeIdentity AFTER INSERT ON GameEnrichmentStates BEGIN UPDATE Games SET IgdbId=132181 WHERE Id=NEW.GameId; END;");
        var result = await f.Service().EnrichTaxonomiesAsync(id);
        Assert.Equal(GameEnrichmentOutcome.IdentityMismatch, result.Outcome); Assert.Equal(0, f.Cooldown.Count);
        using var read = f.Db(); Assert.Equal(974, (await read.Games.SingleAsync()).IgdbId);
        Assert.Empty(await read.CatalogTaxonomies.ToArrayAsync()); Assert.Empty(await read.GameEnrichmentStates.ToArrayAsync());
    }
}
