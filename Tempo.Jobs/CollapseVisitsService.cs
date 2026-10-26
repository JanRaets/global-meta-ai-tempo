using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Jobs;

public sealed record Stay(double Lat, double Lng, DateTimeOffset Start, DateTimeOffset End, bool IsOngoing);

/// <summary>
/// Collapses buffered points into Visit records (design D4, location-ingest spec): a stay of
/// at least 5 minutes within 75 m becomes one Visit; an ongoing stay gets DepartedAt = null
/// and is finalized by a later run. Detection is pure and unit-tested.
/// </summary>
public sealed class CollapseVisitsService(
    TempoDbContext db,
    ILocationBuffer buffer,
    PlaceResolver places,
    IGeocoder geocoder,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<CollapseVisitsService> logger)
{
    private const double StayRadiusM = 75;
    private static readonly TimeSpan MinStay = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan OngoingGrace = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ArrivalMatchTolerance = TimeSpan.FromMinutes(5);

    public static IReadOnlyList<Stay> DetectStays(IReadOnlyList<LocationPoint> points, DateTimeOffset now)
    {
        var stays = new List<Stay>();
        if (points.Count == 0)
            return stays;

        var cluster = new List<LocationPoint> { points[0] };
        double centroidLat = points[0].Lat, centroidLng = points[0].Lng;

        void Flush(bool isLastCluster)
        {
            var duration = cluster[^1].Timestamp - cluster[0].Timestamp;
            if (duration < MinStay)
                return;
            var ongoing = isLastCluster && now - cluster[^1].Timestamp <= OngoingGrace;
            stays.Add(new Stay(centroidLat, centroidLng, cluster[0].Timestamp, cluster[^1].Timestamp, ongoing));
        }

        foreach (var point in points.Skip(1))
        {
            if (GeoMath.DistanceMeters(centroidLat, centroidLng, point.Lat, point.Lng) <= StayRadiusM)
            {
                cluster.Add(point);
                centroidLat += (point.Lat - centroidLat) / cluster.Count;
                centroidLng += (point.Lng - centroidLng) / cluster.Count;
            }
            else
            {
                Flush(isLastCluster: false);
                cluster = [point];
                centroidLat = point.Lat;
                centroidLng = point.Lng;
            }
        }
        Flush(isLastCluster: true);
        return stays;
    }

    public async Task CollapseAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var points = await buffer.GetSinceAsync(now.AddHours(-24), ct);
        var stays = DetectStays(points, now);
        if (stays.Count == 0)
            return;

        var created = new List<Guid>();
        foreach (var stay in stays)
        {
            string? label;
            try
            {
                label = await geocoder.ReverseAsync(stay.Lat, stay.Lng, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Reverse geocode failed, using coordinate label: {Message}", ex.Message);
                label = null;
            }
            var place = await places.ResolvePointAsync(stay.Lat, stay.Lng,
                label ?? $"Stop near {stay.Lat:F3}, {stay.Lng:F3}", ct);

            // DateTimeOffset ranges don't translate on SQLite (tests); visits per place are few.
            var candidates = await db.Visits.Where(v => v.PlaceId == place.Id).ToListAsync(ct);
            var existing = candidates
                .FirstOrDefault(v => (v.ArrivedAt - stay.Start).Duration() <= ArrivalMatchTolerance);
            if (existing is not null)
            {
                existing.DepartedAt = stay.IsOngoing ? null : stay.End;
            }
            else
            {
                var visit = new Visit
                {
                    Id = Guid.NewGuid(),
                    PlaceId = place.Id,
                    ArrivedAt = stay.Start,
                    DepartedAt = stay.IsOngoing ? null : stay.End,
                };
                db.Visits.Add(visit);
                created.Add(visit.Id);
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var id in created)
            await events.PublishAsync(new VisitCreated(id), ct);
        logger.LogInformation("Visit collapse: {Stays} stay(s), {Created} new visit(s)", stays.Count, created.Count);
    }
}

/// <summary>Hangfire entry point: every 10 minutes.</summary>
public sealed class CollapseVisitsJob(CollapseVisitsService collapse)
{
    public Task RunAsync(CancellationToken ct) => collapse.CollapseAsync(ct);
}
