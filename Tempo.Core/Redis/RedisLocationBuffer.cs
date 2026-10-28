using System.Text.Json;
using StackExchange.Redis;
using Tempo.Core.Location;

namespace Tempo.Core.Redis;

/// <summary>
/// Raw points in a Redis list per UTC day (design D4). The 24h TTL is set once when the
/// day key is created (not refreshed per push), so no point outlives 24 hours; reads cover
/// today + yesterday. Nothing here ever touches PostgreSQL.
/// </summary>
public sealed class RedisLocationBuffer(IConnectionMultiplexer redis, TimeProvider clock) : ILocationBuffer
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task AppendAsync(LocationPoint point, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var key = DayKey(clock.GetUtcNow());
        await db.ListRightPushAsync(key, JsonSerializer.Serialize(point, Json));
        await db.KeyExpireAsync(key, TimeSpan.FromHours(24), ExpireWhen.HasNoExpiry);
    }

    public async Task<IReadOnlyList<LocationPoint>> GetSinceAsync(DateTimeOffset since, CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var now = clock.GetUtcNow();
        var points = new List<LocationPoint>();
        foreach (var key in new[] { DayKey(now.AddDays(-1)), DayKey(now) })
        {
            foreach (var value in await db.ListRangeAsync(key))
            {
                var point = JsonSerializer.Deserialize<LocationPoint>((string)value!, Json);
                if (point is not null && point.Timestamp >= since)
                    points.Add(point);
            }
        }
        return points.OrderBy(p => p.Timestamp).ToList();
    }

    public async Task<LocationPoint?> GetLatestAsync(CancellationToken ct = default) =>
        (await GetSinceAsync(clock.GetUtcNow().AddHours(-24), ct)).LastOrDefault();

    public async Task ClearAsync(CancellationToken ct = default)
    {
        var db = redis.GetDatabase();
        var now = clock.GetUtcNow();
        await db.KeyDeleteAsync([(RedisKey)DayKey(now.AddDays(-1)), DayKey(now)]);
    }

    private static string DayKey(DateTimeOffset at) => $"tempo:points:{at:yyyyMMdd}";
}
