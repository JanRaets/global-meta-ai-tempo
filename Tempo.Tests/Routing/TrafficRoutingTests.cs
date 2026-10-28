using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Configuration;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Planning;
using Tempo.Integrations.Maps;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Routing;

/// <summary>Traffic-aware car routing (added 28 Oct 2026): car goes to TomTom (live
/// traffic; ORS in-flight fallback), bicycle/walking stay on ORS, and the traffic delay
/// is a tool-computed fact surfaced on the option from 5 minutes up.</summary>
public sealed class TrafficRoutingTests
{
    private static readonly GeoPoint Zeist = new(52.0906, 5.2332);
    private static readonly GeoPoint School = new(52.0994, 5.1316);

    private const string TomTomBody =
        """{"routes":[{"summary":{"travelTimeInSeconds":1080,"trafficDelayInSeconds":420}}]}""";

    private const string OrsBody =
        """{"routes":[{"summary":{"duration":1920.0,"distance":9000.0}}]}""";

    private static readonly RoutingOptions Options = new()
    {
        ApiKey = "ors-key",
        TomTom = new TomTomOptions { ApiKey = "tomtom-key" },
    };

    private static TomTomRoutingClient TomTom(StubHttpHandler handler, TomTomDailyCounter? counter = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") },
            Microsoft.Extensions.Options.Options.Create(Options), new FakeRoutingCache(),
            counter ?? new TomTomDailyCounter(new FakeTimeProvider()), new FakeTimeProvider(),
            NullLogger<TomTomRoutingClient>.Instance);

    private static OrsRoutingClient Ors(StubHttpHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://unit.test/") },
            Microsoft.Extensions.Options.Options.Create(Options), new FakeRoutingCache(),
            NullLogger<OrsRoutingClient>.Instance);

    private static CompositeRoutingClient Composite(StubHttpHandler tomtom, StubHttpHandler ors, TomTomDailyCounter? counter = null) =>
        new(TomTom(tomtom, counter), Ors(ors), Microsoft.Extensions.Options.Options.Create(Options),
            NullLogger<CompositeRoutingClient>.Instance);

    [Fact]
    public async Task Car_routes_through_tomtom_with_the_traffic_delay()
    {
        var tomtom = new StubHttpHandler((HttpStatusCode.OK, TomTomBody));
        var ors = new StubHttpHandler((HttpStatusCode.OK, OrsBody));

        var estimate = await Composite(tomtom, ors).EstimateAsync(Zeist, School, TravelMode.Car, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(1080), estimate.Duration);
        Assert.Equal(7, estimate.TrafficDelayMin); // 420 s
        Assert.Equal(1, tomtom.Calls);
        Assert.Equal(0, ors.Calls);
    }

    [Fact]
    public async Task Bicycle_routes_through_ors()
    {
        var tomtom = new StubHttpHandler((HttpStatusCode.OK, TomTomBody));
        var ors = new StubHttpHandler((HttpStatusCode.OK, OrsBody));

        var estimate = await Composite(tomtom, ors).EstimateAsync(Zeist, School, TravelMode.Bicycle, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(1920), estimate.Duration);
        Assert.Equal(0, estimate.TrafficDelayMin);
        Assert.Equal(0, tomtom.Calls);
        Assert.Equal(1, ors.Calls);
    }

    [Fact]
    public async Task Tomtom_down_falls_back_to_ors_for_car()
    {
        var tomtom = new StubHttpHandler((HttpStatusCode.InternalServerError, """{"error":"boom"}"""));
        var ors = new StubHttpHandler((HttpStatusCode.OK, OrsBody));

        var estimate = await Composite(tomtom, ors).EstimateAsync(Zeist, School, TravelMode.Car, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(1920), estimate.Duration); // the ORS answer
        Assert.Equal(1, tomtom.Calls);
        Assert.Equal(1, ors.Calls);
    }

    [Fact]
    public async Task Spent_daily_limit_falls_back_to_ors_without_calling_tomtom()
    {
        var counter = new TomTomDailyCounter(new FakeTimeProvider());
        for (var i = 0; i < Options.TomTom.DailyLimit; i++)
            Assert.True(counter.TryTake(Options.TomTom.DailyLimit));
        var tomtom = new StubHttpHandler((HttpStatusCode.OK, TomTomBody));
        var ors = new StubHttpHandler((HttpStatusCode.OK, OrsBody));

        var estimate = await Composite(tomtom, ors, counter).EstimateAsync(Zeist, School, TravelMode.Car, DateTimeOffset.UtcNow);

        Assert.Equal(TimeSpan.FromSeconds(1920), estimate.Duration);
        Assert.Equal(0, tomtom.Calls); // the counter refused before any HTTP left
        Assert.Equal(2500, counter.Today);
    }

    [Fact]
    public async Task Planner_surfaces_the_delay_on_the_option()
    {
        var routing = new FakeRoutingClient();
        routing.TrafficDelayMinutes[TravelMode.Car] = 7;
        var planner = new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "School run",
            Start = DateTimeOffset.Parse("2026-10-29T07:20:00Z"),
            End = DateTimeOffset.Parse("2026-10-29T07:30:00Z"),
            BufferMin = 5,
        };

        var result = await planner.Plan(appointment, Zeist, School, [], [],
            [TravelMode.Car, TravelMode.Bicycle], DateTimeOffset.Parse("2026-10-29T06:00:00Z"));

        Assert.Equal(7, Assert.Single(result.Options, o => o.Mode == TravelMode.Car).TrafficDelayMin);
        Assert.Equal(0, Assert.Single(result.Options, o => o.Mode == TravelMode.Bicycle).TrafficDelayMin);
    }
}
