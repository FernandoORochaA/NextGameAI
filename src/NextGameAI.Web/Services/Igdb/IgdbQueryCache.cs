using Microsoft.Extensions.Caching.Memory;

namespace NextGameAI.Web.Services.Igdb;

// Dedicated bounded cache: never imposes Size requirements on unrelated application caches.
public sealed class IgdbQueryCache(TimeProvider timeProvider) : IDisposable
{
    private readonly MemoryCache cache = new(new MemoryCacheOptions { SizeLimit = 100 });
    public static TimeSpan Lifetime { get; } = TimeSpan.FromMinutes(5);

    public bool TryGet(string key, out IReadOnlyList<IgdbGameResult> games)
    {
        if (cache.TryGetValue(key, out Entry? entry) && entry is not null && entry.ExpiresAt > timeProvider.GetUtcNow())
        { games = entry.Games; return true; }
        cache.Remove(key);
        games = Array.Empty<IgdbGameResult>();
        return false;
    }

    public void Set(string key, IReadOnlyList<IgdbGameResult> games)
    {
        cache.Set(key, new Entry(games, timeProvider.GetUtcNow() + Lifetime),
            new MemoryCacheEntryOptions { Size = 1, AbsoluteExpirationRelativeToNow = Lifetime });
    }

    private sealed record Entry(IReadOnlyList<IgdbGameResult> Games, DateTimeOffset ExpiresAt);
    public void Dispose() => cache.Dispose();
}
