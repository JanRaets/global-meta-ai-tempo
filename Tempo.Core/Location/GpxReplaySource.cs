using System.Runtime.CompilerServices;
using System.Xml.Linq;

namespace Tempo.Core.Location;

/// <summary>A source of location points, posted into the same ingest path as the phone.</summary>
public interface ILocationSource
{
    IAsyncEnumerable<LocationPoint> StreamAsync(CancellationToken ct = default);
}

/// <summary>
/// Replays a GPX trail at a configurable speed for demos and tests (design D11). Track-point
/// time deltas are divided by the speed factor, both for the delay between emitted points and
/// for the emitted timestamps, which are rebased to start at "now".
/// </summary>
public sealed class GpxReplaySource(string gpxXml, double speedFactor, TimeProvider clock) : ILocationSource
{
    public async IAsyncEnumerable<LocationPoint> StreamAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var trackPoints = Parse(gpxXml);
        if (trackPoints.Count == 0)
            yield break;
        var replayStart = clock.GetUtcNow();
        var trailStart = trackPoints[0].Time;
        var previousOffset = TimeSpan.Zero;
        foreach (var (lat, lng, time) in trackPoints)
        {
            var scaledOffset = (time - trailStart) / speedFactor;
            var delay = scaledOffset - previousOffset;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, ct);
            previousOffset = scaledOffset;
            yield return new LocationPoint(lat, lng, replayStart + scaledOffset);
        }
    }

    private static List<(double Lat, double Lng, DateTimeOffset Time)> Parse(string xml) =>
        XDocument.Parse(xml).Descendants()
            .Where(e => e.Name.LocalName == "trkpt")
            .Select(e => (
                Lat: (double)e.Attribute("lat")!,
                Lng: (double)e.Attribute("lon")!,
                Time: DateTimeOffset.Parse(e.Elements().First(c => c.Name.LocalName == "time").Value)))
            .OrderBy(p => p.Time)
            .ToList();
}
