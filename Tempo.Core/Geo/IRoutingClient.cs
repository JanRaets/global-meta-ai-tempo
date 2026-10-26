using Tempo.Core.Domain;

namespace Tempo.Core.Geo;

public sealed record GeoPoint(double Lat, double Lng);

/// <summary>Routing is unreachable or over quota (429/5xx/network). The planner catches this
/// and falls back deterministically (departure-planning spec: Deterministic fallback).</summary>
public sealed class RoutingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Deterministic travel-time estimates (design D6). Only tools call this; the model never sees coordinates.</summary>
public interface IRoutingClient
{
    Task<TimeSpan> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default);

    /// <summary>Total duration from → via… → to in one call; the planner subtracts the base estimate.</summary>
    Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default);
}

/// <summary>5-minute cache for routing durations, keyed on rounded inputs (task 4.2).</summary>
public interface IRoutingCache
{
    Task<double?> GetSecondsAsync(string key, CancellationToken ct = default);
    Task SetSecondsAsync(string key, double seconds, TimeSpan ttl, CancellationToken ct = default);
}
