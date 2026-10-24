using System.Net;

namespace NextGameAI.Web.Services.Igdb;

// One shared outbound lane is intentionally conservative for the local MVP: at most one active request.
public sealed class IgdbRequestScheduler(TimeProvider timeProvider) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private long? lastStarted;
    private DateTimeOffset blockedUntil = DateTimeOffset.MinValue;
    private readonly object state = new();
    public static TimeSpan MinimumSpacing { get; } = TimeSpan.FromMilliseconds(350);
    public static TimeSpan MaximumWait { get; } = TimeSpan.FromSeconds(5);

    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken);
        try
        {
            TimeSpan delay;
            lock (state)
            {
                var spacing = lastStarted.HasValue ? MinimumSpacing - timeProvider.GetElapsedTime(lastStarted.Value) : TimeSpan.Zero;
                var cooldown = blockedUntil - timeProvider.GetUtcNow();
                delay = spacing > cooldown ? spacing : cooldown;
            }
            if (delay > MaximumWait)
                throw new IgdbQueryException(IgdbQueryFailure.RateLimited, HttpStatusCode.TooManyRequests, delay);
            if (delay > TimeSpan.Zero) await Task.Delay(delay, timeProvider, cancellationToken);
            lastStarted = timeProvider.GetTimestamp();
            return await action(cancellationToken);
        }
        finally { gate.Release(); }
    }

    // Called while the response still owns the shared lane, before another request may start.
    public void Defer(TimeSpan delay)
    {
        lock (state)
        {
            var now = timeProvider.GetUtcNow();
            var available = DateTimeOffset.MaxValue - now;
            var until = delay > available ? DateTimeOffset.MaxValue : now + delay;
            if (until > blockedUntil) blockedUntil = until;
        }
    }

    public void Dispose() => gate.Dispose();
}
