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
/// when real data is thin, creates one demo appointment (today 10:00 when that is 20–75 min
/// out — the "coffee before a morning meeting" scene — otherwise now+30), and replays a
/// trail from the home place through the normal ingest path. The trail idles at home until
/// two minutes past the plan's leaveBy and the Late nudge is out, so the full arc lands:
/// Upcoming → LeaveNow → Late (nudge) → EnRoute → Arrived (amended 29 Oct 2026 — earlier
/// fixed-length trails left home inside the margin and the Late beat never happened).
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
        var demoNudges = await db.Nudges.Where(n => n.Source == NudgeSource.Demo).ToListAsync(ct);
        if (demoNudges.Count > 0)
        {
            db.Nudges.RemoveRange(demoNudges);
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Demo mode off: removed {Count} demo nudge(s)", demoNudges.Count);
        }
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

        var target = await EnsureDemoAppointmentAsync(settings, ct);
        if (target is null)
        {
            logger.LogWarning("Demo mode: no appointment to play the arc against — nothing to replay");
            return;
        }
        await ReplayTrailAsync(home, target.Value.Appointment, target.Value.Destination, ct);
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

        // Coffee on weekday mornings before a meeting (every weekday of the last two weeks),
        // groceries on Thursday evenings for three weeks (the evidence behind the habits below).
        var cafeVisits = 0;
        for (var daysBack = 1; daysBack <= 14; daysBack++)
        {
            var day = todayLocal.Date.AddDays(-daysBack).DayOfWeek;
            if (day is >= DayOfWeek.Monday and <= DayOfWeek.Friday)
            {
                Visit(cafe.Id, Past(daysBack, 9, 15), 9);
                cafeVisits++;
            }
        }
        for (var week = 1; week <= 3; week++)
        {
            var thursday = todayLocal.Date.AddDays(-7 * week);
            while (thursday.DayOfWeek != DayOfWeek.Thursday)
                thursday = thursday.AddDays(-1);
            Visit(super.Id, new DateTimeOffset(thursday.AddHours(17).AddMinutes(40), todayLocal.Offset).ToUniversalTime(), 15);
        }

        // The demo habits themselves (amended 29 Oct 2026): the coffee stop is seeded
        // CONFIRMED on any weekday morning before a meeting, so the "coffee before a morning
        // meeting" scene works on every weekday; the supermarket stays a Thursday PROPOSAL so
        // the confirm flow can be shown. Inference-derived habits keep concrete weekdays —
        // the generalised weekday form exists only here (ContextKey, null Weekday).
        db.Habits.Add(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = cafe.Id,
            Context = new ContextKey(null, TimeBucket.Morning, TravelDirection.AwayFromHome, AppointmentKind.Meeting),
            State = HabitState.Confirmed,
            AvgDurationMin = 9,
            VisitCount = cafeVisits,
            ProposalText = "Looks like you usually stop at Bar Bello on weekday mornings before a meeting, about 9 min — want me to plan for it?",
            Rationale = $"{cafeVisits} visits, ~9 min each",
            DecidedAt = clock.GetUtcNow(),
        });
        db.Habits.Add(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = super.Id,
            Context = new ContextKey(DayOfWeek.Thursday, TimeBucket.Evening, TravelDirection.Other, null),
            State = HabitState.Proposed,
            AvgDurationMin = 15,
            VisitCount = 3,
            ProposalText = "Looks like you usually stop at Albert Heijn on Thursday evenings, about 15 min — want me to plan for it?",
            Rationale = "3 visits, ~15 min each",
        });
        await db.SaveChangesAsync(ct);
    }

    private async Task<(Appointment Appointment, Place Destination)?> EnsureDemoAppointmentAsync(
        UserSettings settings, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        // An appointment already in the arc window is the arc: the replay heads for it.
        var existing = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .FirstOrDefault(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddMinutes(60));
        if (existing is not null)
            return (existing, existing.Place!);

        var destination = await places.ResolvePointAsync(52.0894, 5.1100, "Utrecht Centraal", ct);
        await db.SaveChangesAsync(ct);
        // A 10:00 meeting when that is 20–75 min out (the morning filming scene: "Leave in N
        // min" with the weekday-morning coffee stop); otherwise now+30 so the arc works at any
        // hour. Either way leaveAt lands minutes out and lateness follows mid-replay.
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var local = TimeZoneInfo.ConvertTime(now, tz);
        var tenLocal = new DateTimeOffset(local.Date.AddHours(10), local.Offset).ToUniversalTime();
        var start = tenLocal - now >= TimeSpan.FromMinutes(20) && tenLocal - now <= TimeSpan.FromMinutes(75)
            ? tenLocal
            : now.AddMinutes(30);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured, // calendar sync must never delete it
            Title = "Demo: meeting met Tom", // "meeting" so the before-a-meeting coffee habit applies
            Start = start,
            End = start.AddMinutes(60),
            PlaceId = destination.Id,
            BufferMin = 5,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Created), ct);
        logger.LogWarning("Demo mode: seeded appointment '{Title}' at {Start:HH:mm} UTC", appointment.Title, appointment.Start);
        return (appointment, destination);
    }

    /// <summary>13.2 + 13.e (amended 29 Oct 2026): the trail is replayed in real time into
    /// the same ingest path as the phone, in two parts. First it idles at home — jittered
    /// fixes every 15 s — until two minutes past the committed plan's leaveBy AND the Late
    /// nudge has gone out (giving up 6 min past leaveBy, or 25 min in), so the Late beat
    /// always lands. Then a ~3.5-minute GPX drive to the destination: moving away
    /// (EnRoute — no more nudges), ending within the arrived radius (Arrived → "Made it").</summary>
    private async Task ReplayTrailAsync(Place home, Appointment appointment, Place destination, CancellationToken ct)
    {
        // The trail starts at home while the phone may be anywhere: clear the buffer first,
        // or the ingest teleport guard rejects the whole replay (and vice versa on cleanup).
        await buffer.ClearAsync(ct);
        logger.LogWarning("Demo mode: trail {Home} → {Destination} — idling at home until the Late beat, then driving",
            home.Name, destination.Name);

        var started = clock.GetUtcNow();
        for (var i = 0; ; i++)
        {
            var now = clock.GetUtcNow();
            var idle = new LocationPoint(
                home.Lat + 0.0006 * Math.Sin(i / 3.0),
                home.Lng + 0.0006 * Math.Cos(i / 4.0), now);
            if ((await ingest.IngestAsync(idle, ct)).Accepted)
                await phases.EvaluateAllAsync(ct); // phase transitions fire on every post (D6)

            var fresh = await db.UserSettings.AsNoTracking().FirstAsync(ct);
            if (!fresh.DemoMode)
            {
                logger.LogInformation("Demo mode switched off — replay stopped while idling at home");
                return;
            }

            var plan = await db.DeparturePlans.AsNoTracking()
                .Where(p => p.AppointmentId == appointment.Id)
                .OrderByDescending(p => p.Version)
                .FirstOrDefaultAsync(ct);
            if (plan is not null && now > plan.LeaveBy.AddMinutes(2))
            {
                // The Late nudge comes from the 2-min tick; hold at home until it is out so
                // the beat is on screen — but never wait more than 6 min past leaveBy.
                var lateSent = await db.Nudges.AsNoTracking().AnyAsync(n =>
                    n.AppointmentId == appointment.Id && n.Kind == NudgeKind.Late && n.Status == NudgeStatus.Sent, ct);
                if (lateSent || now > plan.LeaveBy.AddMinutes(6))
                {
                    logger.LogWarning("Demo mode: Late beat {State} — starting the drive",
                        lateSent ? "landed" : "NOT observed (giving up waiting)");
                    break;
                }
            }
            if (now - started > TimeSpan.FromMinutes(25))
            {
                logger.LogWarning("Demo mode: no committed plan went late within 25 min — driving anyway");
                break;
            }
            await Task.Delay(TimeSpan.FromSeconds(15), clock, ct);
        }

        var source = new GpxReplaySource(BuildDriveGpx(home, destination, clock.GetUtcNow()), speedFactor: 1, clock);
        var posted = 0;
        await foreach (var point in source.StreamAsync(ct))
        {
            var result = await ingest.IngestAsync(point, ct);
            if (result.Accepted)
            {
                posted++;
                await phases.EvaluateAllAsync(ct);
            }
            var fresh = await db.UserSettings.AsNoTracking().FirstAsync(ct);
            if (!fresh.DemoMode)
            {
                logger.LogInformation("Demo mode switched off — replay stopped after {Posted} drive point(s)", posted);
                return;
            }
        }
        logger.LogWarning("Demo mode: trail replay finished ({Posted} drive point(s) posted)", posted);
    }

    private static string BuildDriveGpx(Place home, Place destination, DateTimeOffset start)
    {
        // 14 points, 15 s apart (~3.5 min): progressively away from home (> EnRouteMeters →
        // EnRoute), the last point inside the arrived radius of the destination (→ Arrived).
        var inv = CultureInfo.InvariantCulture;
        var points = Enumerable.Range(0, 14).Select(i =>
        {
            // fraction of the way home → destination: 3 % … 100 %
            var t = i >= 13 ? 1.0 : 0.03 + i * 0.075;
            var lat = home.Lat + (destination.Lat - home.Lat) * t;
            var lng = home.Lng + (destination.Lng - home.Lng) * t;
            var time = start.AddSeconds((i + 1) * 15).UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ssZ", inv);
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
