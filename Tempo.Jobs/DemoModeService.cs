using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Jobs;

/// <summary>
/// The demo-mode toggle's backing job (13.1/13.2, design D11): seeds the safety net only
/// when real data is thin, creates one demo appointment ~22 minutes out, and replays a
/// generated GPX trail from the home place through the normal ingest path. With a
/// 10-minute nudge lead the window opens within a tick (leave nudge) and lateness follows
/// a few minutes later (late recovery nudge) — the whole arc inside a ~5-minute replay.
/// Everything demo-made is marked "Demo:" so it is recognisable and removable.
/// </summary>
public sealed class DemoModeService(
    TempoDbContext db,
    PlaceResolver places,
    LocationIngestService ingest,
    ILocationBuffer buffer,
    Tempo.Core.Planning.PlanPhaseService phases,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<DemoModeService> logger)
{
    /// <summary>Toggling demo mode off removes what demo mode created: the demo appointment
    /// (plans cascade). Nudges stay as history (plain ids by design); the thin-data seed
    /// stays too — it only ever ran on an empty install.</summary>
    public async Task CleanupAsync(CancellationToken ct = default)
    {
        // The replayed trail left the buffer far from the phone; without a clear, the next
        // real fix reads as an implausible jump and gets rejected (ingest teleport guard).
        await buffer.ClearAsync(ct);
        var demoAppointments = await db.Appointments.Where(a => a.Title.StartsWith("Demo: ")).ToListAsync(ct);
        if (demoAppointments.Count == 0)
            return;
        db.Appointments.RemoveRange(demoAppointments);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Demo mode off: removed {Count} demo appointment(s)", demoAppointments.Count);
    }

    public async Task RunAsync(CancellationToken ct = default)
    {
        var settings = await db.UserSettings.FirstAsync(ct);
        if (!settings.DemoMode)
        {
            logger.LogInformation("Demo mode was switched off again — nothing to do");
            return;
        }

        await SeedIfThinAsync(settings, ct);

        var home = settings.HomePlaceId is { } homeId
            ? await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == homeId, ct)
            : null;
        if (home is null)
        {
            logger.LogWarning("Demo mode: no home place configured — cannot replay a trail");
            return;
        }

        var destination = await EnsureDemoAppointmentAsync(ct);
        await ReplayTrailAsync(home, destination, ct);
    }

    /// <summary>13.1: the seed is a safety net, used only when real data is thin.</summary>
    private async Task SeedIfThinAsync(UserSettings settings, CancellationToken ct)
    {
        var confirmedHabits = await db.Habits.CountAsync(h => h.State == HabitState.Confirmed, ct);
        var visits = await db.Visits.CountAsync(ct);
        if (confirmedHabits > 0 || visits >= 10)
        {
            logger.LogInformation("Demo mode: real data present ({Habits} habits, {Visits} visits) — no seeding",
                confirmedHabits, visits);
            return;
        }

        logger.LogWarning("Demo mode: data is thin — seeding the coffee-week safety net");
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var todayLocal = TimeZoneInfo.ConvertTime(clock.GetUtcNow(), tz);
        var home = await places.ResolvePointAsync(52.0950, 5.2400, "Thuis", ct);
        var cafe = await places.ResolvePointAsync(52.0905, 5.2000, "Bar Bello", ct);
        var super = await places.ResolvePointAsync(52.0930, 5.2100, "Albert Heijn", ct);
        await db.SaveChangesAsync(ct);
        settings.HomePlaceId ??= home.Id;

        void Visit(Guid placeId, DateTimeOffset arrive, int minutes) =>
            db.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = placeId, ArrivedAt = arrive, DepartedAt = arrive.AddMinutes(minutes) });
        DateTimeOffset Past(int daysBack, int hour, int minute) =>
            new DateTimeOffset(todayLocal.Date.AddDays(-daysBack).AddHours(hour).AddMinutes(minute), todayLocal.Offset)
                .ToUniversalTime();

        // Two habit candidates: café on this weekday's morning for three weeks, supermarket
        // on this weekday's early evening — same (weekday, bucket) so inference proposes both.
        for (var week = 1; week <= 3; week++)
        {
            Visit(cafe.Id, Past(7 * week, 8, 50), 9);
            Visit(super.Id, Past(7 * week, 17, 40), 15);
        }
        await db.SaveChangesAsync(ct);
    }

    private async Task<Place> EnsureDemoAppointmentAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var destination = await places.ResolvePointAsync(52.0894, 5.1100, "Utrecht Centraal", ct);
        var upcoming = (await db.Appointments.AsNoTracking().ToListAsync(ct))
            .Any(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddMinutes(60));
        if (upcoming)
            return destination;
        await db.SaveChangesAsync(ct);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured, // calendar sync must never delete it
            // +30: car travel ~25 min puts leaveAt ~5 min out — the leave nudge fires with a
            // future time on the first tick, lateness follows mid-replay (verified live 27 Oct).
            Title = "Demo: borrel met Tom",
            Start = now.AddMinutes(30),
            End = now.AddMinutes(90),
            PlaceId = destination.Id,
            BufferMin = 5,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Created), ct);
        logger.LogWarning("Demo mode: seeded appointment '{Title}' at {Start:HH:mm} UTC", appointment.Title, appointment.Start);
        return destination;
    }

    /// <summary>13.2 + 13.e: a ~10-minute trail replayed in real time into the same ingest
    /// path as the phone. Phase arc: near home while leaveAt passes (leave nudge → late
    /// nudge), then moving away (EnRoute — no more nudges), ending within the arrived
    /// radius of the destination (Arrived → "Made it").</summary>
    private async Task ReplayTrailAsync(Place home, Place destination, CancellationToken ct)
    {
        // The trail starts at home while the phone may be anywhere: clear the buffer first,
        // or the ingest teleport guard rejects the whole replay (and vice versa on cleanup).
        await buffer.ClearAsync(ct);
        var source = new GpxReplaySource(BuildTrailGpx(home, destination, clock.GetUtcNow()), speedFactor: 1, clock);
        var posted = 0;
        logger.LogWarning("Demo mode: replaying the GPX trail {Home} → {Destination} (~10 min)", home.Name, destination.Name);
        await foreach (var point in source.StreamAsync(ct))
        {
            var result = await ingest.IngestAsync(point, ct);
            if (result.Accepted)
            {
                posted++;
                await phases.EvaluateAllAsync(ct); // phase transitions fire on every post (D6)
            }
            var fresh = await db.UserSettings.AsNoTracking().FirstAsync(ct);
            if (!fresh.DemoMode)
            {
                logger.LogInformation("Demo mode switched off — replay stopped after {Posted} point(s)", posted);
                return;
            }
        }
        logger.LogWarning("Demo mode: trail replay finished ({Posted} point(s) posted)", posted);
    }

    private static string BuildTrailGpx(Place home, Place destination, DateTimeOffset start)
    {
        // 40 points, 15 s apart (~10 min). 0–26: jitter within ~70 m of home (Upcoming → Late).
        // 26–34: progressively away from home (> EnRouteMeters → EnRoute). 34–39: closing in
        // on the destination, the last points inside the arrived radius (→ Arrived).
        var inv = CultureInfo.InvariantCulture;
        var points = Enumerable.Range(0, 40).Select(i =>
        {
            double lat, lng;
            if (i < 26)
            {
                lat = home.Lat + 0.0006 * Math.Sin(i / 3.0);
                lng = home.Lng + 0.0006 * Math.Cos(i / 4.0);
            }
            else
            {
                // fraction of the way home → destination: 3 % … 100 %
                var t = i >= 39 ? 1.0 : 0.03 + (i - 26) * 0.075;
                lat = home.Lat + (destination.Lat - home.Lat) * t;
                lng = home.Lng + (destination.Lng - home.Lng) * t;
            }
            var time = start.AddSeconds(i * 15).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);
            return $"<trkpt lat=\"{lat.ToString(inv)}\" lon=\"{lng.ToString(inv)}\"><time>{time}</time></trkpt>";
        });
        return $"<gpx><trk><trkseg>{string.Join("", points)}</trkseg></trk></gpx>";
    }
}

/// <summary>Hangfire entry points, enqueued when the Settings demo toggle flips.</summary>
public sealed class DemoModeJob(DemoModeService demo)
{
    public Task RunAsync(CancellationToken ct) => demo.RunAsync(ct);

    public Task CleanupAsync(CancellationToken ct) => demo.CleanupAsync(ct);
}
