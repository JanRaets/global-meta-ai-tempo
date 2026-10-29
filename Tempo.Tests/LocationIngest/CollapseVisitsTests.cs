using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Core.Location;
using Tempo.Jobs;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.LocationIngest;

public sealed class StayDetectionTests
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-27T09:00:00Z");

    private static List<LocationPoint> PointsAt(double lat, double lng, int fromMin, int toMin, int stepMin = 2) =>
        Enumerable.Range(0, (toMin - fromMin) / stepMin + 1)
            .Select(i => new LocationPoint(lat, lng, T0.AddMinutes(fromMin + i * stepMin)))
            .ToList();

    [Fact]
    public void Twenty_minutes_within_75m_is_one_stay()
    {
        // Points jitter ~20 m around the centroid.
        var points = new List<LocationPoint>();
        for (var minute = 0; minute <= 20; minute += 2)
            points.Add(new LocationPoint(52.0900 + (minute % 4 == 0 ? 0.0002 : -0.0002), 5.1200, T0.AddMinutes(minute)));

        var stays = CollapseVisitsService.DetectStays(points, T0.AddHours(2));

        var stay = Assert.Single(stays);
        Assert.Equal(T0, stay.Start);
        Assert.Equal(T0.AddMinutes(20), stay.End);
        Assert.False(stay.IsOngoing);
        Assert.Equal(52.0900, stay.Lat, precision: 3);
    }

    [Fact]
    public void Continuous_movement_yields_no_stay()
    {
        // ~500 m every 2 minutes.
        var points = Enumerable.Range(0, 15)
            .Select(i => new LocationPoint(52.00 + i * 0.005, 5.12, T0.AddMinutes(i * 2)))
            .ToList();

        Assert.Empty(CollapseVisitsService.DetectStays(points, T0.AddHours(1)));
    }

    [Fact]
    public void A_recent_last_cluster_is_an_ongoing_stay()
    {
        var points = PointsAt(52.09, 5.12, fromMin: 0, toMin: 12);

        var stays = CollapseVisitsService.DetectStays(points, now: T0.AddMinutes(14));

        Assert.True(Assert.Single(stays).IsOngoing);
    }

    [Fact]
    public void Short_pauses_under_5_minutes_are_ignored()
    {
        var points = PointsAt(52.09, 5.12, fromMin: 0, toMin: 4);
        points.AddRange(PointsAt(52.20, 5.30, fromMin: 10, toMin: 13));

        Assert.Empty(CollapseVisitsService.DetectStays(points, T0.AddHours(1)));
    }
}

public sealed class CollapseVisitsServiceTests : IDisposable
{
    private static readonly DateTimeOffset T0 = DateTimeOffset.Parse("2026-10-27T09:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeGeocoder geocoder = new() { ReverseLabel = "Bar Bello, Utrecht" };
    private readonly CollectingDomainEvents events = new();
    private readonly FakeTimeProvider clock = new(T0.AddMinutes(14));

    private CollapseVisitsService Service() => new(
        db.Context, buffer, new PlaceResolver(db.Context, geocoder), geocoder, events, clock,
        NullLogger<CollapseVisitsService>.Instance);

    private async Task AddStayPointsAsync(int fromMin, int toMin)
    {
        for (var minute = fromMin; minute <= toMin; minute += 2)
            await buffer.AppendAsync(new LocationPoint(52.09, 5.12, T0.AddMinutes(minute)));
    }

    [Fact]
    public async Task A_stay_becomes_a_visit_with_a_reverse_geocoded_place()
    {
        await AddStayPointsAsync(0, 12);
        clock.SetUtcNow(T0.AddMinutes(40)); // stay has clearly ended

        await Service().CollapseAsync();

        var visit = Assert.Single(db.Context.Visits);
        Assert.Equal(T0, visit.ArrivedAt);
        Assert.Equal(T0.AddMinutes(12), visit.DepartedAt);
        Assert.Equal("Bar Bello, Utrecht", Assert.Single(db.Context.Places).Name);
        Assert.Single(events.Published.OfType<VisitCreated>());
    }

    [Fact]
    public async Task An_ongoing_stay_is_created_open_and_closed_by_a_later_run()
    {
        await AddStayPointsAsync(0, 12);

        await Service().CollapseAsync(); // now = T0+14 → ongoing
        var visit = Assert.Single(db.Context.Visits);
        Assert.Null(visit.DepartedAt);

        await AddStayPointsAsync(14, 20);
        clock.SetUtcNow(T0.AddMinutes(45)); // user left; last point 25 min ago
        await Service().CollapseAsync();

        visit = Assert.Single(db.Context.Visits); // updated, not duplicated
        Assert.Equal(T0.AddMinutes(20), visit.DepartedAt);
        Assert.Single(events.Published.OfType<VisitCreated>()); // only the first run created one
    }

    // Residue of concurrent collapse runs (live, 28 Oct): two open visits describing the
    // same stay. The earliest arrival is the stay; the later duplicate goes (29 Oct 2026).
    [Fact]
    public async Task Duplicate_open_visits_at_the_same_place_keep_only_the_earliest()
    {
        var place = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.09, Lng = 5.12 };
        db.Context.Places.Add(place);
        db.Context.Visits.AddRange(
            new Visit { Id = Guid.NewGuid(), PlaceId = place.Id, ArrivedAt = T0.AddHours(-5), DepartedAt = null },
            new Visit { Id = Guid.NewGuid(), PlaceId = place.Id, ArrivedAt = T0.AddHours(-2), DepartedAt = null });
        await db.Context.SaveChangesAsync();

        await Service().CollapseAsync();

        var visit = Assert.Single(db.Context.Visits);
        Assert.Equal(T0.AddHours(-5), visit.ArrivedAt);
        Assert.Null(visit.DepartedAt); // still the ongoing stay
    }

    [Fact]
    public async Task Transit_creates_nothing()
    {
        for (var i = 0; i < 15; i++)
            await buffer.AppendAsync(new LocationPoint(52.00 + i * 0.005, 5.12, T0.AddMinutes(i * 2)));

        await Service().CollapseAsync();

        Assert.Empty(db.Context.Visits);
    }

    public void Dispose() => db.Dispose();
}
