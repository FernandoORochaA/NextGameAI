using Microsoft.Extensions.DependencyInjection;
using NextGameAI.Web.Services;
using NextGameAI.Web.Services.Igdb;

namespace NextGameAI.Web.Tests;

[Trait("Host", "None")]
public sealed class GameEnrichmentCoordinatorTests
{
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Timeouts only detect hangs; ordering is established by signals and synchronous admission to WaitAsync.
    private static readonly TimeSpan Deadline = TimeSpan.FromSeconds(15);

    private static void ObserveWait(GameEnrichmentCoordinator coordinator, Action? observer) =>
        typeof(GameEnrichmentCoordinator).GetProperty("WriterWaitRegistered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.SetValue(coordinator, observer);

    [Theory]
    [InlineData("held")] [InlineData("released")] [InlineData("cancel-first")] [InlineData("release-first")]
    [InlineData("dispose-game")] [InlineData("aggregate-fatal")]
    public async Task FatalObserverClosesRegisteredWaitBeforePropagatingAndNextWriterWorks(string scenario)
    {
        var c = new GameEnrichmentCoordinator();
        using var a = await c.AcquireGameAsync(1); var b = await c.AcquireGameAsync(2); using var nextGame = await c.AcquireGameAsync(3);
        var first = await a.AcquireWriteAsync(); using var cancellation = new CancellationTokenSource(); var registered = Signal();
        Exception fault = scenario == "aggregate-fatal"
            ? new AggregateException(new InvalidOperationException("Synthetic"), new OutOfMemoryException("Synthetic object only"))
            : new OutOfMemoryException("Synthetic object only, no exhaustion");
        ObserveWait(c, () =>
        {
            registered.TrySetResult();
            if (scenario == "cancel-first") { cancellation.Cancel(); first.Dispose(); }
            if (scenario is "released" or "release-first") { first.Dispose(); if (scenario == "release-first") cancellation.Cancel(); }
            if (scenario == "dispose-game") b.Dispose();
            throw fault;
        });
        var failed = b.AcquireWriteAsync(cancellation.Token); await registered.Task.WaitAsync(Deadline);
        Assert.Same(fault, await Record.ExceptionAsync(() => failed.WaitAsync(Deadline)));
        ObserveWait(c, null);
        first.Dispose(); b.Dispose();
        using var next = await nextGame.AcquireWriteAsync().WaitAsync(Deadline);
        // A stale permission or Release double would allow this writer to enter concurrently.
        var waiting = a.AcquireWriteAsync(); Assert.False(waiting.IsCompleted);
        next.Dispose(); (await waiting.WaitAsync(Deadline)).Dispose();
        a.Dispose(); nextGame.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task AvailableWriterDoesNotInvokeWaitObserverAndDoubleDisposeRemainsSafe()
    {
        var c = new GameEnrichmentCoordinator(); using var game = await c.AcquireGameAsync(1); var calls = 0;
        ObserveWait(c, () => { calls++; throw new OutOfMemoryException("Synthetic observer must not run without waiting"); });
        var write = await game.AcquireWriteAsync().WaitAsync(Deadline);
        Assert.Equal(0, calls); write.Dispose(); await write.DisposeAsync();
        using var again = await game.AcquireWriteAsync().WaitAsync(Deadline); Assert.Equal(0, calls);
    }

    [Fact]
    public async Task CancelAndAcquisitionCanRaceWithoutLostOrDuplicateGlobalPermission()
    {
        for (var round = 0; round < 30; round++)
        {
            var c = new GameEnrichmentCoordinator(); using var a = await c.AcquireGameAsync(1); using var b = await c.AcquireGameAsync(2);
            var held = await a.AcquireWriteAsync(); using var cancel = new CancellationTokenSource(); var registered = Signal(); var start = Signal();
            ObserveWait(c, () => registered.TrySetResult());
            var pending = b.AcquireWriteAsync(cancel.Token); await registered.Task.WaitAsync(Deadline);
            var release = Task.Run(async () => { await start.Task; held.Dispose(); });
            var cancellation = Task.Run(async () => { await start.Task; cancel.Cancel(); });
            start.SetResult(); await Task.WhenAll(release, cancellation).WaitAsync(Deadline);
            try { (await pending.WaitAsync(Deadline)).Dispose(); }
            catch (OperationCanceledException) { }
            ObserveWait(c, null);
            using var next = await a.AcquireWriteAsync().WaitAsync(Deadline);
            var another = b.AcquireWriteAsync(); Assert.False(another.IsCompleted);
            next.Dispose(); (await another.WaitAsync(Deadline)).Dispose();
            a.Dispose(); b.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
        }
    }

    [Fact]
    public async Task DiagnosticObserverFailureDoesNotStrandRegisteredWriterWait()
    {
        var c = new GameEnrichmentCoordinator(); using var a = await c.AcquireGameAsync(1); using var b = await c.AcquireGameAsync(2);
        var write = await a.AcquireWriteAsync(); var waiting = Signal();
        typeof(GameEnrichmentCoordinator).GetProperty("WriterWaitRegistered", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(c, (Action)(() => { waiting.TrySetResult(); throw new InvalidOperationException("Synthetic observer failure"); }));
        var pending = b.AcquireWriteAsync(); await waiting.Task.WaitAsync(Deadline); Assert.False(pending.IsCompleted);
        write.Dispose(); (await pending.WaitAsync(Deadline)).Dispose(); a.Dispose(); b.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task AttemptSequencesAreUniqueAcrossConcurrentLeasesAndCoordinators()
    {
        var c = new GameEnrichmentCoordinator(); var start = Signal();
        var tasks = Enumerable.Range(1, 32).Select(i => Task.Run(async () =>
        {
            await start.Task; using var lease = await c.AcquireGameAsync(i);
            var ids = Enumerable.Range(0, 20).Select(_ => lease.CreateAttempt(NextGameAI.Web.Models.CatalogMetadataSection.Genres)).ToArray();
            Assert.All(ids.Zip(ids.Skip(1)), p => Assert.True(p.First.Sequence < p.Second.Sequence));
            Assert.All(ids, id => Assert.Equal(i, id.GameId)); return ids;
        })).ToArray();
        start.SetResult(); var all = (await Task.WhenAll(tasks).WaitAsync(Deadline)).SelectMany(x => x).ToArray();
        Assert.Equal(640, all.Select(x => x.Sequence).Distinct().Count());
        var next = await new GameEnrichmentCoordinator().AcquireGameAsync(1);
        Assert.True(next.CreateAttempt(NextGameAI.Web.Models.CatalogMetadataSection.Themes).Sequence > all.Max(x => x.Sequence));
        Assert.Throws<ArgumentOutOfRangeException>(() => next.CreateAttempt((NextGameAI.Web.Models.CatalogMetadataSection)99));
        next.Dispose(); Assert.Throws<ObjectDisposedException>(() => next.CreateAttempt(NextGameAI.Web.Models.CatalogMetadataSection.Genres));
    }

    [Fact]
    public async Task SameGameWaitsAndDifferentGameRunsIndependently()
    {
        var c = new GameEnrichmentCoordinator();
        var first = await c.AcquireGameAsync(1);
        var waiting = c.AcquireGameAsync(1);
        Assert.False(waiting.IsCompleted);
        using var other = await c.AcquireGameAsync(2).WaitAsync(Deadline);
        Assert.Equal(2, c.RegisteredGameCount);
        first.Dispose();
        var second = await waiting.WaitAsync(Deadline);
        first.Dispose(); // Must not accidentally release the second owner.
        var third = c.AcquireGameAsync(1); Assert.False(third.IsCompleted);
        second.Dispose(); (await third.WaitAsync(Deadline)).Dispose();
        other.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task ManyWaitersAndConcurrentCreationNeverProduceTwoOwners()
    {
        var c = new GameEnrichmentCoordinator(); var start = Signal();
        var firstEntered = Signal(); var releaseFirst = Signal();
        var owners = 0; var admissions = 0;
        var tasks = Enumerable.Range(0, 32).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            await using var lease = await c.AcquireGameAsync(1);
            Assert.Equal(1, Interlocked.Increment(ref owners));
            if (Interlocked.Increment(ref admissions) == 1)
            { firstEntered.SetResult(); await releaseFirst.Task; }
            await Task.Yield();
            Assert.Equal(0, Interlocked.Decrement(ref owners));
        })).ToArray();
        start.SetResult(); await firstEntered.Task.WaitAsync(Deadline);
        Assert.Equal(1, c.RegisteredGameCount);
        releaseFirst.SetResult(); await Task.WhenAll(tasks).WaitAsync(Deadline);
        Assert.Equal(32, admissions); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task TwelveAlreadyAdmittedWaitersShareOneEntry()
    {
        var c = new GameEnrichmentCoordinator(1); var first = await c.AcquireGameAsync(1);
        var waiting = Enumerable.Range(0, 12).Select(_ => c.AcquireGameAsync(1)).ToArray();
        Assert.All(waiting, task => Assert.False(task.IsCompleted));
        Assert.Equal(1, c.RegisteredGameCount);
        first.Dispose();
        var owners = 0;
        await Task.WhenAll(waiting.Select(async task =>
        {
            using var lease = await task;
            Assert.Equal(1, Interlocked.Increment(ref owners));
            await Task.Yield(); Interlocked.Decrement(ref owners);
        })).WaitAsync(Deadline);
        Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task CapacityRejectsNewGamesWithoutEvictingOwnersOrWaiters()
    {
        var c = new GameEnrichmentCoordinator(2);
        var first = await c.AcquireGameAsync(1); var last = await c.AcquireGameAsync(2);
        await Assert.ThrowsAsync<EnrichmentCapacityException>(() => c.AcquireGameAsync(3));
        using var cancelled = new CancellationTokenSource();
        var wait = c.AcquireGameAsync(1, cancelled.Token);
        Assert.False(wait.IsCompleted); Assert.Equal(2, c.RegisteredGameCount);
        cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.Equal(2, c.RegisteredGameCount);
        first.Dispose(); Assert.Equal(1, c.RegisteredGameCount);
        using var recovered = await c.AcquireGameAsync(3);
        Assert.Equal(2, c.RegisteredGameCount);
        last.Dispose(); recovered.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotCancelOwnerOrOtherWaiter()
    {
        var c = new GameEnrichmentCoordinator(); var owner = await c.AcquireGameAsync(1);
        using var cancel = new CancellationTokenSource();
        var cancelled = c.AcquireGameAsync(1, cancel.Token); var survivor = c.AcquireGameAsync(1);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.False(survivor.IsCompleted); Assert.Equal(1, c.RegisteredGameCount);
        owner.Dispose(); (await survivor.WaitAsync(Deadline)).Dispose();
        Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task CancellationBeforeAndAfterAcquisitionAndExceptionsReleaseGame()
    {
        var c = new GameEnrichmentCoordinator(); using var cancel = new CancellationTokenSource(); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.AcquireGameAsync(1, cancel.Token));
        Assert.Equal(0, c.RegisteredGameCount);
        using var after = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await using var lease = await c.AcquireGameAsync(1, after.Token);
            after.Cancel(); after.Token.ThrowIfCancellationRequested();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        { using var lease = await c.AcquireGameAsync(1); throw new InvalidOperationException("Synthetic"); });
        using var next = await c.AcquireGameAsync(1).WaitAsync(Deadline);
        next.Dispose(); Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task GlobalWriterExcludesDifferentGamesButNotTheirIndependentWork()
    {
        var c = new GameEnrichmentCoordinator();
        using var a = await c.AcquireGameAsync(1); using var b = await c.AcquireGameAsync(2);
        // Holding a game alone does not occupy the global writer (e.g. external-query phase).
        var wb = await b.AcquireWriteAsync().WaitAsync(Deadline);
        var wa = a.AcquireWriteAsync(); Assert.False(wa.IsCompleted);
        wb.Dispose(); var active = await wa.WaitAsync(Deadline);
        wb.Dispose(); var next = b.AcquireWriteAsync(); Assert.False(next.IsCompleted);
        active.Dispose(); (await next.WaitAsync(Deadline)).Dispose();
    }

    [Fact]
    public async Task WriteRequiresActiveGameAndRejectsReentrance()
    {
        var c = new GameEnrichmentCoordinator(); var game = await c.AcquireGameAsync(1);
        using var write = await game.AcquireWriteAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => game.AcquireWriteAsync());
        game.Dispose();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => game.AcquireWriteAsync());
        var next = c.AcquireGameAsync(1); Assert.False(next.IsCompleted);
        write.Dispose(); (await next.WaitAsync(Deadline)).Dispose(); Assert.Equal(0, c.RegisteredGameCount);
        Assert.DoesNotContain(typeof(GameEnrichmentCoordinator).GetMethods(), m => m.Name == "AcquireWriteAsync");
    }

    [Fact]
    public async Task CancelledGlobalWaitCanBeRetriedWithoutLosingGame()
    {
        var c = new GameEnrichmentCoordinator();
        using var a = await c.AcquireGameAsync(1); using var b = await c.AcquireGameAsync(2);
        using var wa = await a.AcquireWriteAsync(); using var cancel = new CancellationTokenSource();
        var wait = b.AcquireWriteAsync(cancel.Token); Assert.False(wait.IsCompleted);
        cancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        wa.Dispose(); using var wb = await b.AcquireWriteAsync().WaitAsync(Deadline);
        Assert.Equal(2, c.RegisteredGameCount);
    }

    [Fact]
    public async Task DisposingGameDuringGlobalWaitPinsEntryUntilWaitFinishes()
    {
        var c = new GameEnrichmentCoordinator();
        using var a = await c.AcquireGameAsync(1); var b = await c.AcquireGameAsync(2);
        using var write = await a.AcquireWriteAsync();
        var pending = b.AcquireWriteAsync(); b.Dispose();
        var gameWait = c.AcquireGameAsync(2); Assert.False(gameWait.IsCompleted);
        write.Dispose(); await Assert.ThrowsAsync<ObjectDisposedException>(() => pending);
        (await gameWait.WaitAsync(Deadline)).Dispose(); a.Dispose();
        Assert.Equal(0, c.RegisteredGameCount);
    }

    [Fact]
    public async Task WriteCancellationAndExceptionReleaseBothLeases()
    {
        var c = new GameEnrichmentCoordinator(); using var cancel = new CancellationTokenSource();
        using (var game = await c.AcquireGameAsync(1))
        {
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => game.AcquireWriteAsync(cancel.Token));
            using var write = await game.AcquireWriteAsync();
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            using var game = await c.AcquireGameAsync(1); using var token = new CancellationTokenSource();
            await using var write = await game.AcquireWriteAsync(token.Token);
            token.Cancel(); token.Token.ThrowIfCancellationRequested();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var game = await c.AcquireGameAsync(1); using var write = await game.AcquireWriteAsync();
            throw new InvalidOperationException("Synthetic");
        });
        using var nextGame = await c.AcquireGameAsync(1).WaitAsync(Deadline);
        using var nextWrite = await nextGame.AcquireWriteAsync().WaitAsync(Deadline);
    }

    [Fact]
    public async Task ConcurrentDisposalAndReacquisitionDoNotReuseRetiredEntry()
    {
        var c = new GameEnrichmentCoordinator(1);
        for (var round = 0; round < 30; round++)
        {
            var old = await c.AcquireGameAsync(1);
            var start = Signal(); var owners = 0;
            var disposals = Enumerable.Range(0, 8).Select(_ => Task.Run(async () => { await start.Task; old.Dispose(); })).ToArray();
            var contenders = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                await start.Task; using var lease = await c.AcquireGameAsync(1);
                Assert.Equal(1, Interlocked.Increment(ref owners)); await Task.Yield(); Interlocked.Decrement(ref owners);
            })).ToArray();
            start.SetResult(); await Task.WhenAll(disposals.Concat(contenders)).WaitAsync(Deadline);
            Assert.Equal(0, c.RegisteredGameCount);
            using var newGame = await c.AcquireGameAsync(2); old.Dispose(); Assert.Equal(1, c.RegisteredGameCount);
        }
    }

    [Fact]
    public async Task ConcurrentWriteDisposalCannotReleaseAnotherOwner()
    {
        var c = new GameEnrichmentCoordinator();
        using var a = await c.AcquireGameAsync(1); using var b = await c.AcquireGameAsync(2);
        var write = await a.AcquireWriteAsync(); var next = b.AcquireWriteAsync();
        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(write.Dispose))).WaitAsync(Deadline);
        using var second = await next.WaitAsync(Deadline);
        var third = a.AcquireWriteAsync(); Assert.False(third.IsCompleted);
        second.Dispose(); (await third.WaitAsync(Deadline)).Dispose();
    }

    [Fact]
    public async Task InvalidInputDoesNotRegisterEntriesAndDiSharesSingleton()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new GameEnrichmentCoordinator(0));
        var c = new GameEnrichmentCoordinator();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => c.AcquireGameAsync(0));
        Assert.Equal(0, c.RegisteredGameCount);
        var services = new ServiceCollection(); services.AddIgdbSearch();
        using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        Assert.Same(provider.GetRequiredService<GameEnrichmentCoordinator>(), provider.GetRequiredService<GameEnrichmentCoordinator>());
    }
}
