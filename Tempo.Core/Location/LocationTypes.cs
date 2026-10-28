using Tempo.Core.Geo;

namespace Tempo.Core.Location;

public sealed record LocationPoint(double Lat, double Lng, DateTimeOffset Timestamp, double? AccuracyM = null);

/// <summary>24-hour raw point buffer (design D4). Points never reach PostgreSQL.</summary>
public interface ILocationBuffer
{
    Task AppendAsync(LocationPoint point, CancellationToken ct = default);
    Task<IReadOnlyList<LocationPoint>> GetSinceAsync(DateTimeOffset since, CancellationToken ct = default);
    Task<LocationPoint?> GetLatestAsync(CancellationToken ct = default);
}

public sealed record IngestResult(bool Accepted, string? Reason = null)
{
    public static readonly IngestResult Ok = new(true);
    public static IngestResult Rejected(string reason) => new(false, reason);
}

/// <summary>Validates and buffers points (location-ingest spec: Location point ingestion).
/// Besides range/staleness, rejects coarse fixes and physically implausible jumps — live on
/// 28 Oct a Mac browser tab posted an IP-geolocated fix from another continent, which became
/// a plan's leave reference and produced a nonsense route.</summary>
public sealed class LocationIngestService(ILocationBuffer buffer, TimeProvider clock)
{
    private const double MaxAccuracyMeters = 150;          // IP geolocation reports km-scale accuracy
    private const double MaxPlausibleSpeedMps = 70;        // ~250 km/h — nothing TEMPO plans moves faster
    private static readonly TimeSpan ReanchorAfter = TimeSpan.FromHours(3); // after a long gap, trust the new fix

    public async Task<IngestResult> IngestAsync(LocationPoint point, CancellationToken ct = default)
    {
        if (point.Lat is < -90 or > 90 || point.Lng is < -180 or > 180)
            return IngestResult.Rejected("coordinates out of range");
        var now = clock.GetUtcNow();
        if (point.Timestamp < now.AddHours(-24))
            return IngestResult.Rejected("timestamp older than 24h");
        if (point.Timestamp > now.AddMinutes(5))
            return IngestResult.Rejected("timestamp more than 5 min in the future");
        if (point.AccuracyM is > MaxAccuracyMeters)
            return IngestResult.Rejected($"accuracy {point.AccuracyM:0} m is too coarse (over {MaxAccuracyMeters} m)");

        var previous = await buffer.GetLatestAsync(ct);
        if (previous is not null)
        {
            var elapsed = point.Timestamp - previous.Timestamp;
            if (elapsed > TimeSpan.Zero && elapsed < ReanchorAfter)
            {
                var meters = GeoMath.DistanceMeters(previous.Lat, previous.Lng, point.Lat, point.Lng);
                if (meters > 1000 && meters / elapsed.TotalSeconds > MaxPlausibleSpeedMps)
                    return IngestResult.Rejected(
                        $"implausible jump: {meters / 1000:0.#} km in {elapsed.TotalMinutes:0.#} min");
            }
        }

        await buffer.AppendAsync(point, ct);
        return IngestResult.Ok;
    }
}

public sealed record CurrentLocation(LocationPoint Point, double AgeMinutes, bool IsStale);

/// <summary>location.current(): latest buffered point and its age; stale after 30 minutes,
/// at which point the planner falls back to the home place (location-ingest spec).</summary>
public sealed class CurrentLocationProvider(ILocationBuffer buffer, TimeProvider clock)
{
    public async Task<CurrentLocation?> GetAsync(CancellationToken ct = default)
    {
        var latest = await buffer.GetLatestAsync(ct);
        if (latest is null)
            return null;
        var ageMinutes = (clock.GetUtcNow() - latest.Timestamp).TotalMinutes;
        return new CurrentLocation(latest, ageMinutes, IsStale: ageMinutes > 30);
    }
}
