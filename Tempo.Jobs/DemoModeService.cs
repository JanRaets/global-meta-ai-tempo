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
    IDomainEvents events,
    TimeProvider clock,
    ILogger<DemoModeService> logger)
{
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

        await EnsureDemoAppointmentAsync(ct);
        await ReplayTrailAsync(home, ct);
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

    private async Task EnsureDemoAppointmentAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var upcoming = (await db.Appointments.AsNoTracking().ToListAsync(ct))
            .Any(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddMinutes(60));
        if (upcoming)
            return;
        var destination = await places.ResolvePointAsync(52.0894, 5.1100, "Utrecht Centraal", ct);
        await db.SaveChangesAsync(ct);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured, // calendar sync must never delete it
            Title = "Demo: borrel met Tom",
            Start = now.AddMinutes(22),
            End = now.AddMinutes(82),
            PlaceId = destination.Id,
            BufferMin = 5,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Created), ct);
        logger.LogWarning("Demo mode: seeded appointment '{Title}' at {Start:HH:mm} UTC", appointment.Title, appointment.Start);
    }

    /// <summary>13.2: a ~5½-minute trail of points around the home place, replayed in real
    /// time into the same ingest path as the phone. Staying near home while leaveAt passes
    /// is what turns the leave nudge into a late one.</summary>
    private async Task ReplayTrailAsync(Place home, CancellationToken ct)
    {
        var source = new GpxReplaySource(BuildTrailGpx(home, clock.GetUtcNow()), speedFactor: 1, clock);
        var posted = 0;
        logger.LogWarning("Demo mode: replaying the GPX trail from {Home} (~5 min)", home.Name);
        await foreach (var point in source.StreamAsync(ct))
        {
            var result = await ingest.IngestAsync(point, ct);
            if (result.Accepted)
                posted++;
            var fresh = await db.UserSettings.AsNoTracking().FirstAsync(ct);
            if (!fresh.DemoMode)
            {
                logger.LogInformation("Demo mode switched off — replay stopped after {Posted} point(s)", posted);
                return;
            }
        }
        logger.LogWarning("Demo mode: trail replay finished ({Posted} point(s) posted)", posted);
    }

    private static string BuildTrailGpx(Place home, DateTimeOffset start)
    {
        // 22 points, 15 s apart (~5½ min), wandering within ~80 m of home. The wander also
        // changes the rounded location in the tick's inputs hash, which wakes the agent.
        var inv = CultureInfo.InvariantCulture;
        var points = Enumerable.Range(0, 22).Select(i =>
        {
            var lat = home.Lat + 0.0006 * Math.Sin(i / 3.0);
            var lng = home.Lng + 0.0006 * Math.Cos(i / 4.0);
            var time = start.AddSeconds(i * 15).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);
            return $"<trkpt lat=\"{lat.ToString(inv)}\" lon=\"{lng.ToString(inv)}\"><time>{time}</time></trkpt>";
        });
        return $"<gpx><trk><trkseg>{string.Join("", points)}</trkseg></trk></gpx>";
    }
}

/// <summary>Hangfire entry point, enqueued when the Settings demo toggle flips on.</summary>
public sealed class DemoModeJob(DemoModeService demo)
{
    public Task RunAsync(CancellationToken ct) => demo.RunAsync(ct);
}
