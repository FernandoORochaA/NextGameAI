namespace NextGameAI.Web.Services;

public readonly record struct EnrichmentAttemptId
{
    public long Sequence { get; }
    public int GameId { get; }
    public Models.CatalogMetadataSection Section { get; }
    internal EnrichmentAttemptId(long sequence, int gameId, Models.CatalogMetadataSection section)
    { Sequence = sequence; GameId = gameId; Section = section; }
}

public sealed class EnrichmentCapacityException() : InvalidOperationException("A capacidade de operações de enriquecimento foi atingida.");

// Process-local, inert until explicitly acquired. Never retains database/HTTP services.
// Acquire one game, then its write lease, then (in a future integration) a transaction.
// Do not nest different game leases: this component does not track a caller's logical execution context.
public sealed class GameEnrichmentCoordinator(int capacity = 1024)
{
    // 1024 is deliberately generous for the local MVP, while bounding distinct active/waiting games.
    private readonly int capacity = capacity > 0 ? capacity : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly object gate = new();
    private readonly Dictionary<int, Entry> entries = [];
    private readonly SemaphoreSlim writer = new(1, 1);
    // Internal diagnostic seam: signals an actually registered waiter, without exposing game data.
    // Observer failures must never change lock ownership or strand the pending wait.
    internal Action? WriterWaitRegistered { get; set; }

    private void NotifyWriterWait()
    {
        try { WriterWaitRegistered?.Invoke(); }
        catch (Exception error) when (EnrichmentExceptionPolicy.IsManaged(error)) { }
    }
    private static long lastAttempt;

    private static long NextAttempt()
    {
        while (true)
        {
            var previous = Volatile.Read(ref lastAttempt);
            if (previous == long.MaxValue) throw new InvalidOperationException("A sequência de tentativas foi esgotada.");
            if (Interlocked.CompareExchange(ref lastAttempt, previous + 1, previous) == previous) return previous + 1;
        }
    }
    internal sealed class Entry
    {
        internal readonly SemaphoreSlim Semaphore = new(1, 1);
        internal int References;
    }

    // Diagnostics only; no game identifiers or application data are exposed.
    public int RegisteredGameCount { get { lock (gate) return entries.Count; } }

    public async Task<GameLease> AcquireGameAsync(int gameId, CancellationToken cancellationToken = default)
    {
        if (gameId <= 0) throw new ArgumentOutOfRangeException(nameof(gameId));
        cancellationToken.ThrowIfCancellationRequested();
        Entry entry;
        lock (gate)
        {
            if (!entries.TryGetValue(gameId, out entry!))
            {
                if (entries.Count >= capacity) throw new EnrichmentCapacityException();
                entries.Add(gameId, entry = new());
            }
            entry.References++;
        }
        try
        {
            await entry.Semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            return new GameLease(this, gameId, entry);
        }
        catch { ReturnReference(gameId, entry, false); throw; }
    }

    private void ReturnReference(int gameId, Entry entry, bool acquired)
    {
        lock (gate)
        {
            if (acquired) entry.Semaphore.Release();
            entry.References--;
            if (entry.References == 0 && entries.TryGetValue(gameId, out var current) && ReferenceEquals(current, entry))
            {
                entries.Remove(gameId);
                entry.Semaphore.Dispose();
            }
        }
    }

    public sealed class GameLease : IDisposable, IAsyncDisposable
    {
        private readonly GameEnrichmentCoordinator owner;
        private readonly int gameId;
        private readonly Entry entry;
        private readonly object sync = new();
        private bool disposed, writing;
        internal GameLease(GameEnrichmentCoordinator owner, int gameId, Entry entry)
        { this.owner = owner; this.gameId = gameId; this.entry = entry; }

        // Issuing an identity does not record an outcome or confirm any database write.
        public EnrichmentAttemptId CreateAttempt(Models.CatalogMetadataSection section)
        {
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
                return new(NextAttempt(), gameId, section);
            }
        }

        // There is intentionally no coordinator-level AcquireWrite: an active game lease is required.
        public async Task<WriteLease> AcquireWriteAsync(CancellationToken cancellationToken = default)
        {
            using var waitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (sync)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                if (writing) throw new InvalidOperationException("Já existe aquisição de escrita pendente ou ativa neste lease.");
                cancellationToken.ThrowIfCancellationRequested();
                writing = true;
            }
            Task? pending = null;
            var acquired = false;
            try
            {
                pending = owner.writer.WaitAsync(waitCancellation.Token);
                if (!pending.IsCompleted) owner.NotifyWriterWait();
                await pending.ConfigureAwait(false);
                acquired = true;
                lock (sync) ObjectDisposedException.ThrowIf(disposed, this);
                return new WriteLease(this);
            }
            catch
            {
                try
                {
                    if (pending is not null && !acquired)
                    {
                        // Cancel is not enough: observe completion, including a concurrent successful acquisition.
                        waitCancellation.Cancel();
                        try { await pending.ConfigureAwait(false); acquired = true; }
                        catch (OperationCanceledException) { }
                    }
                }
                finally
                {
                    if (acquired) owner.writer.Release();
                    FinishWriting();
                }
                throw;
            }
        }

        private void FinishWriting()
        {
            lock (sync)
            {
                writing = false;
                if (disposed) owner.ReturnReference(gameId, entry, true);
            }
        }
        public void Dispose()
        {
            lock (sync)
            {
                if (disposed) return;
                disposed = true;
                // A pending/active child pins the game until it finishes, even under incorrect disposal order.
                if (!writing) owner.ReturnReference(gameId, entry, true);
            }
        }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }

        public sealed class WriteLease : IDisposable, IAsyncDisposable
        {
            private GameLease? parent;
            internal WriteLease(GameLease parent) => this.parent = parent;
            public void Dispose()
            {
                var lease = Interlocked.Exchange(ref parent, null);
                if (lease is null) return;
                lease.owner.writer.Release();
                lease.FinishWriting();
            }
            public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
        }
    }
}
