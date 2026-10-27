using StackExchange.Redis;
using Tempo.Core.Geo;

namespace Tempo.Core.Redis;

/// <summary>Hit/miss counters for the routing cache (13.c): /admin/model-usage shows them,
/// proving the 2-minute tick reuses cached estimates instead of hammering ORS.</summary>
public sealed class RoutingCacheStats
{
    private long hits;
    private long misses;

    public long Hits => Interlocked.Read(ref hits);
    public long Misses => Interlocked.Read(ref misses);

    public void Hit() => Interlocked.Increment(ref hits);
    public void Miss() => Interlocked.Increment(ref misses);
}

public sealed class RedisRoutingCache(IConnectionMultiplexer redis, RoutingCacheStats stats) : IRoutingCache
{
    public async Task<double?> GetSecondsAsync(string key, CancellationToken ct = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        if (value.HasValue)
            stats.Hit();
        else
            stats.Miss();
        return value.HasValue ? (double)value : null;
    }

    public Task SetSecondsAsync(string key, double seconds, TimeSpan ttl, CancellationToken ct = default) =>
        redis.GetDatabase().StringSetAsync(key, seconds, ttl);
}
