using Tempo.Core.Domain;

namespace Tempo.Core.Geo;

public sealed record GeoPoint(double Lat, double Lng);

/// <summary>Routing is unreachable or over quota (429/5xx/network). The planner catches this
/// and falls back deterministically (departure-planning spec: Deterministic fallback).</summary>
public sealed class RoutingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>A routed duration plus the traffic share inside it (zero for providers without
/// live traffic). The delay is a fact computed by tools, never by the model (D1).</summary>
public sealed record RouteEstimate(TimeSpan Duration, TimeSpan TrafficDelay)
{
    public int TrafficDelayMin => (int)Math.Round(TrafficDelay.TotalMinutes);
    public static RouteEstimate NoTraffic(TimeSpan duration) => new(duration, TimeSpan.Zero);
}

/// <summary>Deterministic travel-time estimates (design D6). Only tools call this; the model never sees coordinates.</summary>
public interface IRoutingClient
{
    Task<RouteEstimate> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default);

    /// <summary>Total duration from → via… → to in one call; the planner subtracts the base estimate.</summary>
    Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default);
}

/// <summary>TomTom's free tier allows 2,500 routing calls per day; the counter guards the
/// limit (calls past it fall back to ORS) and reports on /admin/model-usage.</summary>
public sealed class TomTomDailyCounter(TimeProvider clock)
{
    private readonly object gate = new();
    private DateOnly day;
    private int count;

    public int Today
    {
        get { lock (gate) { Roll(); return count; } }
    }

    /// <summary>Counts one call; false when the daily limit is already spent.</summary>
    public bool TryTake(int dailyLimit)
    {
        lock (gate)
        {
            Roll();
            if (count >= dailyLimit)
                return false;
            count++;
            return true;
        }
    }

    private void Roll()
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        if (today != day)
        {
            day = today;
            count = 0;
        }
    }
}

/// <summary>5-minute cache for routing durations, keyed on rounded inputs (task 4.2).</summary>
public interface IRoutingCache
{
    Task<double?> GetSecondsAsync(string key, CancellationToken ct = default);
    Task SetSecondsAsync(string key, double seconds, TimeSpan ttl, CancellationToken ct = default);
}
