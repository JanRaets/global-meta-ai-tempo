using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Location;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.LocationIngest;

public sealed class LocationIngestTests
{
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T10:00:00Z"));
    private readonly FakeLocationBuffer buffer = new();

    private LocationIngestService Service() => new(buffer, clock);

    [Fact]
    public async Task Valid_point_is_buffered_and_accepted()
    {
        var result = await Service().IngestAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow().AddMinutes(-1), 12));

        Assert.True(result.Accepted);
        Assert.Single(buffer.Points);
    }

    [Theory]
    [InlineData(91, 5.12)]
    [InlineData(52.09, 181)]
    [InlineData(-91, -181)]
    public async Task Out_of_range_coordinates_are_rejected(double lat, double lng)
    {
        var result = await Service().IngestAsync(new LocationPoint(lat, lng, clock.GetUtcNow()));

        Assert.False(result.Accepted);
        Assert.Empty(buffer.Points);
    }

    [Fact]
    public async Task Point_older_than_24h_is_rejected()
    {
        var result = await Service().IngestAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow().AddHours(-25)));

        Assert.False(result.Accepted);
        Assert.Contains("24h", result.Reason);
    }

    [Fact]
    public async Task Point_from_the_future_is_rejected()
    {
        var result = await Service().IngestAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow().AddMinutes(10)));

        Assert.False(result.Accepted);
    }

    [Fact]
    public async Task Coarse_accuracy_is_rejected()
    {
        // IP-geolocated browser fixes report km-scale accuracy; they must never enter the buffer.
        var result = await Service().IngestAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow(), 5000));

        Assert.False(result.Accepted);
        Assert.Contains("coarse", result.Reason);
    }

    [Fact]
    public async Task A_point_without_accuracy_is_still_accepted()
    {
        var result = await Service().IngestAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow(), null));

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task An_intercontinental_teleport_is_rejected()
    {
        // Live 28 Oct: a Mac browser tab posted a Santa Clara fix minutes after a phone fix at home.
        await Service().IngestAsync(new LocationPoint(52.079, 5.2587, clock.GetUtcNow().AddMinutes(-10), 10));

        var result = await Service().IngestAsync(new LocationPoint(37.354, -121.955, clock.GetUtcNow(), 65));

        Assert.False(result.Accepted);
        Assert.Contains("implausible jump", result.Reason);
        Assert.Single(buffer.Points);
    }

    [Fact]
    public async Task Ordinary_driving_speed_is_accepted()
    {
        await Service().IngestAsync(new LocationPoint(52.0790, 5.2587, clock.GetUtcNow().AddMinutes(-2), 10));

        // ~2.7 km in 2 min ≈ 80 km/h — a normal stretch of N-road.
        var result = await Service().IngestAsync(new LocationPoint(52.0790, 5.2197, clock.GetUtcNow(), 10));

        Assert.True(result.Accepted);
        Assert.Equal(2, buffer.Points.Count);
    }

    [Fact]
    public async Task After_a_long_gap_a_far_away_fix_reanchors()
    {
        // Phone off on a flight: the first fix after 3+ hours is trusted wherever it lands.
        await Service().IngestAsync(new LocationPoint(52.079, 5.2587, clock.GetUtcNow().AddHours(-4), 10));

        var result = await Service().IngestAsync(new LocationPoint(37.354, -121.955, clock.GetUtcNow(), 65));

        Assert.True(result.Accepted);
    }

    [Fact]
    public async Task Current_location_is_stale_after_30_minutes_and_fresh_before()
    {
        await buffer.AppendAsync(new LocationPoint(52.09, 5.12, clock.GetUtcNow()));
        var provider = new CurrentLocationProvider(buffer, clock);

        clock.Advance(TimeSpan.FromMinutes(29));
        Assert.False((await provider.GetAsync())!.IsStale);

        clock.Advance(TimeSpan.FromMinutes(2));
        var current = await provider.GetAsync();
        Assert.True(current!.IsStale);
        Assert.Equal(31, current.AgeMinutes, precision: 0);
    }

    [Fact]
    public async Task No_point_yields_null_current_location()
    {
        Assert.Null(await new CurrentLocationProvider(buffer, clock).GetAsync());
    }
}

public sealed class OwntracksMappingTests
{
    [Fact]
    public void Location_message_maps_to_a_standard_point()
    {
        var point = OwntracksMessage.TryMap("""{"_type":"location","lat":52.09,"lon":5.23,"tst":1761500000,"acc":12}""");

        Assert.NotNull(point);
        Assert.Equal(52.09, point.Lat);
        Assert.Equal(5.23, point.Lng);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1761500000), point.Timestamp);
        Assert.Equal(12, point.AccuracyM);
    }

    [Theory]
    [InlineData("""{"_type":"lwt","tst":1761500000}""")]
    [InlineData("""{"_type":"transition","lat":52.09,"lon":5.23,"tst":1761500000}""")]
    [InlineData("""{"lat":52.09,"lon":5.23}""")]
    [InlineData("not json at all")]
    public void Non_location_messages_map_to_nothing(string json)
    {
        Assert.Null(OwntracksMessage.TryMap(json));
    }
}
