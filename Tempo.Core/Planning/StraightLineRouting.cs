using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Core.Planning;

/// <summary>
/// Deterministic straight-line estimator (design D18): haversine distance × 1.3 road factor
/// at mode speeds. Used only when the real routing provider is unavailable for every enabled
/// mode; every option built from it is marked estimated and worded "roughly".
/// </summary>
public sealed class StraightLineRouting : IRoutingClient
{
    private const double RoadFactor = 1.3;

    private static readonly Dictionary<TravelMode, double> KmPerHour = new()
    {
        [TravelMode.Bicycle] = 15,
        [TravelMode.Walking] = 4.8,
        [TravelMode.Car] = 35,
    };

    public Task<TimeSpan> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default) =>
        Task.FromResult(Leg(from, to, mode));

    public Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default)
    {
        var points = new List<GeoPoint> { from };
        points.AddRange(via);
        points.Add(to);
        var total = TimeSpan.Zero;
        for (var i = 1; i < points.Count; i++)
            total += Leg(points[i - 1], points[i], mode);
        return Task.FromResult(total);
    }

    private static TimeSpan Leg(GeoPoint from, GeoPoint to, TravelMode mode)
    {
        if (!KmPerHour.TryGetValue(mode, out var speed))
            throw new RoutingUnavailableException($"no straight-line speed for {mode}");
        var km = GeoMath.DistanceMeters(from.Lat, from.Lng, to.Lat, to.Lng) / 1000 * RoadFactor;
        return TimeSpan.FromHours(km / speed);
    }
}
