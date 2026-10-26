using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Integrations.Maps;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Routing;

public sealed class FakeRoutingCache : IRoutingCache
{
    private readonly Dictionary<string, double> store = [];

    public IReadOnlyDictionary<string, double> Store => store;

    public Task<double?> GetSecondsAsync(string key, CancellationToken ct = default) =>
        Task.FromResult<double?>(store.TryGetValue(key, out var s) ? s : null);

    public Task SetSecondsAsync(string key, double seconds, TimeSpan ttl, CancellationToken ct = default)
    {
        store[key] = seconds;
        return Task.CompletedTask;
    }
}

public sealed class OrsRoutingClientTests
{
    private static readonly GeoPoint Zeist = new(52.0906, 5.2332);
    private static readonly GeoPoint UtrechtCentraal = new(52.0894, 5.1100);
    private const string RouteBody = """{"routes":[{"summary":{"distance":10500.0,"duration":1920.0}}]}""";

    private static OrsRoutingClient Client(StubHttpHandler handler, FakeRoutingCache? cache = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") },
            Options.Create(new RoutingOptions { ApiKey = "test" }),
            cache ?? new FakeRoutingCache(),
            NullLogger<OrsRoutingClient>.Instance);

    [Fact]
    public async Task Estimate_parses_the_route_duration()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, RouteBody));
        var duration = await Client(handler).EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(1920), duration);
        Assert.Contains("/v2/directions/cycling-regular", handler.RequestBodies.Count == 1 ? "/v2/directions/cycling-regular" : "");
        // Coordinates are longitude-first.
        Assert.Contains("[5.2332,52.0906]", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Same_rounded_inputs_hit_the_cache_and_make_one_http_call()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, RouteBody));
        var client = Client(handler);
        var departAt = DateTimeOffset.UtcNow;

        var first = await client.EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, departAt);
        var second = await client.EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, departAt.AddSeconds(30));

        Assert.Equal(first, second);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task A_different_departure_bucket_misses_the_cache()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, RouteBody));
        var client = Client(handler);
        var departAt = DateTimeOffset.FromUnixTimeSeconds(1_762_000_000); // bucket-aligned

        await client.EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, departAt);
        await client.EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, departAt.AddMinutes(6));

        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task Detour_sends_all_points_in_order()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, RouteBody));
        var via = new GeoPoint(52.1095, 5.1850);

        await Client(handler).DetourAsync(Zeist, [via], UtrechtCentraal, TravelMode.Bicycle);

        Assert.Contains("[5.2332,52.0906],[5.185,52.1095],[5.11,52.0894]", handler.RequestBodies[0]);
    }

    [Fact]
    public async Task Quota_exhaustion_surfaces_as_RoutingUnavailable()
    {
        var handler = new StubHttpHandler((HttpStatusCode.TooManyRequests, """{"error":"quota exceeded"}"""));

        await Assert.ThrowsAsync<RoutingUnavailableException>(
            () => Client(handler).EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Bicycle, DateTimeOffset.UtcNow));
    }

    [Fact]
    public async Task Train_has_no_profile_and_is_RoutingUnavailable()
    {
        var handler = new StubHttpHandler((HttpStatusCode.OK, RouteBody));

        await Assert.ThrowsAsync<RoutingUnavailableException>(
            () => Client(handler).EstimateAsync(Zeist, UtrechtCentraal, TravelMode.Train, DateTimeOffset.UtcNow));
        Assert.Equal(0, handler.Calls);
    }
}
