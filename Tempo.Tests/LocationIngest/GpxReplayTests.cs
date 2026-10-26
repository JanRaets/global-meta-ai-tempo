using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Location;

namespace Tempo.Tests.LocationIngest;

public sealed class GpxReplayTests
{
    private const string Gpx = """
        <?xml version="1.0" encoding="UTF-8"?>
        <gpx version="1.1" xmlns="http://www.topografix.com/GPX/1/1">
          <trk><trkseg>
            <trkpt lat="52.0900" lon="5.1200"><time>2026-10-20T08:00:00Z</time></trkpt>
            <trkpt lat="52.0910" lon="5.1210"><time>2026-10-20T08:05:00Z</time></trkpt>
            <trkpt lat="52.0920" lon="5.1220"><time>2026-10-20T08:10:00Z</time></trkpt>
          </trkseg></trk>
        </gpx>
        """;

    [Fact]
    public async Task Replays_points_in_order_with_scaled_timestamps()
    {
        var start = DateTimeOffset.Parse("2026-10-27T12:00:00Z");
        var clock = new FakeTimeProvider(start);
        var source = new GpxReplaySource(Gpx, speedFactor: 3000, clock); // 5 min → 100 ms

        var points = new List<LocationPoint>();
        await foreach (var point in source.StreamAsync())
            points.Add(point);

        Assert.Equal(3, points.Count);
        Assert.Equal(start, points[0].Timestamp);
        Assert.Equal(start.AddSeconds(0.1), points[1].Timestamp); // 300 s / 3000
        Assert.Equal(start.AddSeconds(0.2), points[2].Timestamp);
        Assert.Equal(52.0900, points[0].Lat);
        Assert.Equal(52.0920, points[2].Lat);
    }
}
