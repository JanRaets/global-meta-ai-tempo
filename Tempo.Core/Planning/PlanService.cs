using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Core.Planning;

/// <summary>
/// Gathers planner inputs (appointment, location with home fallback, settings, confirmed
/// habits, per-plan input changes), runs DeparturePlanner, and guards plan.commit:
/// only an optionId from the latest plan.options call in this scope commits, and a commit
/// that changes nothing beyond ±3 min routing jitter keeps the current version.
/// </summary>
public sealed class PlanService(
    TempoDbContext db,
    DeparturePlanner planner,
    PlanOptionStore store,
    CurrentLocationProvider location,
    TimeProvider clock,
    ILogger<PlanService> logger)
{
    private static readonly TimeSpan JitterTolerance = TimeSpan.FromMinutes(3);

    private sealed record PlanContext(
        Appointment Appointment, UserSettings Settings, Place? Home, GeoPoint Origin, PlanInputs? Inputs, ContextKey Key);

    public async Task<PlanOptionsResult> OptionsAsync(Guid appointmentId, CancellationToken ct = default)
    {
        var (ctx, note) = await BuildContextAsync(appointmentId, ct);
        if (ctx is null)
            return PlanOptionsResult.Empty(note!);

        var effectiveNow = clock.GetUtcNow().AddMinutes(ctx.Inputs?.LateOffsetMin ?? 0);
        var habits = await MatchingHabitsAsync(ctx, ct);

        var extraStops = new List<PlannerStop>();
        foreach (var extra in ctx.Inputs?.ExtraStops ?? [])
        {
            var place = await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == extra.PlaceId, ct);
            if (place is not null)
                extraStops.Add(new PlannerStop(place.Id, extra.Name, extra.DurationMin, new GeoPoint(place.Lat, place.Lng)));
        }

        var pinned = ctx.Inputs?.PinnedMode ?? ctx.Settings.PinnedMode;
        var modes = pinned is { } pin ? [pin] : ctx.Settings.EnabledModes;

        var result = await planner.Plan(ctx.Appointment, ctx.Origin,
            new GeoPoint(ctx.Appointment.Place!.Lat, ctx.Appointment.Place.Lng),
            habits, extraStops, modes, effectiveNow, ct);
        store.ReplaceFor(appointmentId, result.Options);
        return result;
    }

    /// <summary>Confirmed habits matching this appointment's planning context (habits.confirmed tool),
    /// with per-plan skipped stops already removed — the same set the planner uses.</summary>
    public async Task<IReadOnlyList<ConfirmedHabit>> ConfirmedMatchingAsync(Guid appointmentId, CancellationToken ct = default)
    {
        var (ctx, _) = await BuildContextAsync(appointmentId, ct);
        return ctx is null ? [] : await MatchingHabitsAsync(ctx, ct);
    }

    /// <summary>plan.commit: persists the chosen option as the current plan version. Rejects any
    /// optionId not produced by the latest plan.options call in this scope (design D1 guard).</summary>
    public async Task<CommitResult> CommitAsync(string optionId, string reason, PlanCommitter committer, CancellationToken ct = default)
    {
        if (store.TryGet(optionId) is not var (appointmentId, option))
            return CommitResult.Rejected($"unknown optionId '{optionId}' — it was not returned by the latest plan.options call");

        var current = await db.DeparturePlans
            .Where(p => p.AppointmentId == appointmentId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

        if (current is not null
            && current.Mode == option.Mode
            && SameStops(current.Stops, option.Stops)
            && (current.LeaveAt - option.LeaveAt).Duration() <= JitterTolerance)
        {
            return CommitResult.Unchanged(current); // ±3 min routing jitter: version unchanged
        }

        var plan = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointmentId,
            Version = (current?.Version ?? 0) + 1,
            Mode = option.Mode,
            LeaveAt = option.LeaveAt,
            ArriveAt = option.ArriveAt,
            IsFeasible = option.IsFeasible,
            SlackMin = option.SlackMin,
            Stops = option.Stops.ToList(),
            Reason = reason,
            CommittedBy = committer,
        };
        db.DeparturePlans.Add(plan);
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Plan v{Version} committed for {AppointmentId}: {Mode}, leave {LeaveAt:HH:mm} ({Committer})",
            plan.Version, appointmentId, plan.Mode, plan.LeaveAt, committer);
        return CommitResult.Committed(plan);
    }

    public Task<DeparturePlan?> CurrentAsync(Guid appointmentId, CancellationToken ct = default) =>
        db.DeparturePlans
            .Where(p => p.AppointmentId == appointmentId)
            .OrderByDescending(p => p.Version)
            .FirstOrDefaultAsync(ct);

    /// <summary>Deterministic fallback (departure-planning spec): when no agent run commits,
    /// commit the feasible option with the latest leaveAt — or the least-late one — ourselves.</summary>
    public async Task<DeparturePlan?> FallbackCommitAsync(Guid appointmentId, CancellationToken ct = default)
    {
        var result = await OptionsAsync(appointmentId, ct);
        if (result.Options.Count == 0)
        {
            logger.LogWarning("Fallback commit impossible for {AppointmentId}: {Note}", appointmentId, result.Note);
            return null;
        }
        var pick = result.Options.Where(o => o.IsFeasible).OrderByDescending(o => o.LeaveAt).FirstOrDefault()
            ?? result.Options.OrderByDescending(o => o.SlackMin).First();
        var commit = await CommitAsync(pick.OptionId, "fallback: model unavailable", PlanCommitter.Fallback, ct);
        return commit.Plan;
    }

    private async Task<(PlanContext? Ctx, string? Note)> BuildContextAsync(Guid appointmentId, CancellationToken ct)
    {
        var appointment = await db.Appointments.Include(a => a.Place).SingleOrDefaultAsync(a => a.Id == appointmentId, ct);
        if (appointment is null)
            return (null, "appointment not found");
        if (appointment.Place is null)
            return (null, "appointment has no resolvable place");

        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var homePlace = settings.HomePlaceId is { } homeId
            ? await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == homeId, ct)
            : null;

        var current = await location.GetAsync(ct);
        GeoPoint origin;
        if (current is { IsStale: false })
            origin = new GeoPoint(current.Point.Lat, current.Point.Lng);
        else if (homePlace is not null)
            origin = new GeoPoint(homePlace.Lat, homePlace.Lng); // stale/no location → home (location-ingest spec)
        else if (current is not null)
            origin = new GeoPoint(current.Point.Lat, current.Point.Lng); // stale but better than nothing
        else
            return (null, "no current location and no home place configured");

        var inputs = await db.PlanInputs.AsNoTracking().SingleOrDefaultAsync(p => p.AppointmentId == appointmentId, ct);
        var key = BuildPlanningContext(appointment, settings, homePlace, origin);
        return (new PlanContext(appointment, settings, homePlace, origin, inputs, key), null);
    }

    private async Task<IReadOnlyList<ConfirmedHabit>> MatchingHabitsAsync(PlanContext ctx, CancellationToken ct)
    {
        var skipped = ctx.Inputs?.SkippedPlaceIds ?? [];
        // A stop on the way happens shortly before the appointment, so its habit bucket may be
        // the one preceding the appointment's (08:50 coffee before a 09:30 meeting: Early vs
        // Morning). Match both buckets (D5 note, 27 Oct).
        var preKey = ctx.Key with { TimeBucket = PreviousBucket(ctx.Key.TimeBucket) };
        return (await db.Habits.Include(h => h.Place)
                .Where(h => h.State == HabitState.Confirmed && !h.IsPaused)
                .ToListAsync(ct))
            .Select(ConfirmHabitHandler.FromPersisted)
            .Where(h => (h.Matches(ctx.Key) || h.Matches(preKey)) && !skipped.Contains(h.PlaceId))
            .ToList();
    }

    private static TimeBucket PreviousBucket(TimeBucket bucket) => bucket switch
    {
        TimeBucket.Morning => TimeBucket.Early,
        TimeBucket.Midday => TimeBucket.Morning,
        TimeBucket.Afternoon => TimeBucket.Midday,
        TimeBucket.Evening => TimeBucket.Afternoon,
        _ => TimeBucket.Evening, // Early wraps to the previous evening
    };

    private static bool SameStops(IReadOnlyList<PlanStop> a, IReadOnlyList<PlanStop> b) =>
        a.Select(s => s.PlaceId).Order().SequenceEqual(b.Select(s => s.PlaceId).Order());

    /// <summary>Planning context for habit matching (design D5). The adjacent-appointment kind
    /// is the appointment's own kind, classified deterministically from its title.</summary>
    private static ContextKey BuildPlanningContext(Appointment appointment, UserSettings settings, Place? homePlace, GeoPoint origin)
    {
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var localStart = TimeZoneInfo.ConvertTime(appointment.Start, tz);
        var direction =
            appointment.PlaceId is not null && appointment.PlaceId == settings.HomePlaceId ? TravelDirection.TowardHome
            : homePlace is not null && GeoMath.DistanceMeters(origin.Lat, origin.Lng, homePlace.Lat, homePlace.Lng) <= 100
                ? TravelDirection.AwayFromHome
            : TravelDirection.Other;
        return new ContextKey(localStart.DayOfWeek, ContextKey.BucketOf(TimeOnly.FromDateTime(localStart.DateTime)), direction,
            AppointmentKind.Classify(appointment.Title));
    }
}
