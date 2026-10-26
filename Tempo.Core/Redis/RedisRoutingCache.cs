using StackExchange.Redis;
using Tempo.Core.Geo;

namespace Tempo.Core.Redis;

public sealed class RedisRoutingCache(IConnectionMultiplexer redis) : IRoutingCache
{
    public async Task<double?> GetSecondsAsync(string key, CancellationToken ct = default)
    {
        var value = await redis.GetDatabase().StringGetAsync(key);
        return value.HasValue ? (double)value : null;
    }

    public Task SetSecondsAsync(string key, double seconds, TimeSpan ttl, CancellationToken ct = default) =>
        redis.GetDatabase().StringSetAsync(key, seconds, ttl);
}
