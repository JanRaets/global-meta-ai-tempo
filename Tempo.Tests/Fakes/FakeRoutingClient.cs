using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Tests.Fakes;

public sealed class FakeRoutingClient : IRoutingClient
{
    public Dictionary<TravelMode, double> BaseMinutes { get; } = new()
    {
        [TravelMode.Bicycle] = 18,
        [TravelMode.Walking] = 60,
        [TravelMode.Car] = 12,
    };

    /// <summary>Flat extra minutes any detour call adds on top of the base estimate.</summary>
    public double DetourExtraMin { get; set; } = 4;

    public HashSet<TravelMode> Unavailable { get; } = [];
    public List<GeoPoint> Origins { get; } = [];

    public Task<TimeSpan> EstimateAsync(GeoPoint from, GeoPoint to, TravelMode mode, DateTimeOffset departAt, CancellationToken ct = default)
    {
        if (Unavailable.Contains(mode))
            throw new RoutingUnavailableException($"fake: {mode} unavailable");
        Origins.Add(from);
        return Task.FromResult(TimeSpan.FromMinutes(BaseMinutes[mode]));
    }

    public Task<TimeSpan> DetourAsync(GeoPoint from, IReadOnlyList<GeoPoint> via, GeoPoint to, TravelMode mode, CancellationToken ct = default)
    {
        if (Unavailable.Contains(mode))
            throw new RoutingUnavailableException($"fake: {mode} unavailable");
        return Task.FromResult(TimeSpan.FromMinutes(BaseMinutes[mode] + DetourExtraMin));
    }
}
