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
    ILogger<PlanService> logger,
    Tempo.Core.Configuration.PhaseOptions? phaseOptions = null)
{
    private static readonly TimeSpan JitterTolerance = TimeSpan.FromMinutes(3);
    private readonly Tempo.Core.Configuration.PhaseOptions phase = phaseOptions ?? new();

    private sealed record PlanContext(
        Appointment Appointment, UserSettings Settings, Place? Home, GeoPoint Origin, PlanInputs? Inputs, ContextKey Key,
        OriginKind OriginKind, string? OriginName);

    public async Task<PlanOptionsResult> OptionsAsync(Guid appointmentId, CancellationToken ct = default)
    {
        var (ctx, note) = await BuildContextAsync(appointmentId, ct);
        if (ctx is null)
            return PlanOptionsResult.Empty(note!);

        // Outcome amendment (28 Oct 2026): a past appointment shows what happened, never a
        // plan — no options, no routing calls, the last committed version stays frozen.
        // Started-long-ago counts too (live: a multi-day calendar event got a car plan for
        // a start two days in the past); the arrived-fallback window still allows late recovery.
        var nowUtc = clock.GetUtcNow();
        if (ctx.Appointment.End <= nowUtc
            || nowUtc >= ctx.Appointment.Start.AddMinutes(phase.ArrivedFallbackMin))
            return PlanOptionsResult.Empty("the appointment is already over — its plan is frozen");

        // Already at (or chained to) the destination: zero travel, no stops, no habit
        // matching, leaveAt = start (D6 amendment, 28 Oct 2026 — two meetings at the same
        // office must not produce a travel plan and a coffee stop).
        var destination = new GeoPoint(ctx.Appointment.Place!.Lat, ctx.Appointment.Place.Lng);
        if (GeoMath.DistanceMeters(ctx.Origin.Lat, ctx.Origin.Lng, destination.Lat, destination.Lng) <= phase.ArrivedMeters)
        {
            var zero = new PlanOption(
                OptionId: Guid.NewGuid().ToString("N")[..8],
                Mode: TravelMode.Walking,
                Stops: [],
                LeaveAt: ctx.Appointment.Start,
                LeaveBy: ctx.Appointment.Start,
                ArriveAt: ctx.Appointment.Start,
                IsFeasible: true,
                SlackMin: 0,
                OriginKind: ctx.OriginKind,
                OriginName: ctx.OriginName);
            store.ReplaceFor(appointmentId, [zero]);
            return new PlanOptionsResult([zero], "no travel — already at the destination");
        }

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

        var result = await planner.Plan(ctx.Appointment, ctx.Origin, destination,
            habits, extraStops, modes, effectiveNow, ct);
        var stamped = result.Options
            .Select(o => o with { OriginKind = ctx.OriginKind, OriginName = ctx.OriginName })
            .ToList();
        store.ReplaceFor(appointmentId, stamped);
        return new PlanOptionsResult(stamped, result.Note);
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
            && current.IsEstimated == option.IsEstimated // estimate → real is a real change (D18)
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
            LeaveBy = option.LeaveBy,
            ArriveAt = option.ArriveAt,
            IsFeasible = option.IsFeasible,
            SlackMin = option.SlackMin,
            Stops = option.Stops.ToList(),
            Reason = reason,
            CommittedBy = committer,
            IsEstimated = option.IsEstimated,
            // Phase is journey state, not plan-version state (D6, amended 28 Oct after a live
            // mid-drive recommit reset it to Upcoming and let a "leave now" nudge through):
            // a new version inherits where the user already is on the way.
            Phase = current?.Phase ?? PlanPhase.Upcoming,
            PhaseChangedAt = current?.PhaseChangedAt,
            LeaveFixLat = current?.LeaveFixLat,
            LeaveFixLng = current?.LeaveFixLng,
            OriginKind = option.OriginKind,
            OriginName = option.OriginName,
            TrafficDelayMin = option.TrafficDelayMin,
        };
        // Why did leaveAt move versus the previous version? (replacement-nudge amendment,
        // 29 Oct 2026) Classified deterministically from what changed since that version:
        // an explicit user intent wins, then an edited appointment, then a different stop
        // set, then the traffic rule; Routing is the remainder.
        if (current is not null && (current.LeaveAt - option.LeaveAt).Duration() >= TimeSpan.FromMinutes(1))
        {
            var appointmentRow = await db.Appointments.AsNoTracking().SingleOrDefaultAsync(a => a.Id == appointmentId, ct);
            var inputs = await db.PlanInputs.AsNoTracking().SingleOrDefaultAsync(p => p.AppointmentId == appointmentId, ct);
            plan.ShiftReason =
                inputs is not null && inputs.UpdatedAt > current.CreatedAt ? PlanShiftReason.Intent
                : appointmentRow is not null && appointmentRow.UpdatedAt > current.CreatedAt ? PlanShiftReason.Appointment
                : !SameStops(current.Stops, option.Stops) ? PlanShiftReason.Habit
                : TrafficOrRouting(current.TrafficDelayMin, option.TrafficDelayMin,
                    (current.LeaveAt - option.LeaveAt).TotalMinutes);
            plan.ShiftedFromLeaveAt = current.LeaveAt;
        }
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

    /// <summary>13.e bug 1 + D18 recovery: while Late/EnRoute — or while the plan is an
    /// estimate — the committed option's times must track reality. Re-runs the planner
    /// deterministically (no model call) and refreshes the committed option in place —
    /// same mode, same stops, same version, no nudge. `BetterAlternative` means another
    /// option now arrives more than 5 minutes earlier; `RecoveredWithShift` means routing
    /// answered again and real times moved leaveAt by more than 5 minutes. Either one is
    /// an input change: the caller wakes the agent.</summary>
    public async Task<(DeparturePlan Plan, bool BetterAlternative, bool RecoveredWithShift)?> RefreshCommittedAsync(
        Guid appointmentId, CancellationToken ct = default)
    {
        var plan = await CurrentAsync(appointmentId, ct);
        if (plan is null)
            return null;
        var result = await OptionsAsync(appointmentId, ct);
        if (result.Options.Count == 0)
            return (plan, false, false);

        var match = result.Options.FirstOrDefault(o => o.Mode == plan.Mode && SameStops(plan.Stops, o.Stops));
        if (match is null)
            return (plan, true, false); // the chosen combination no longer exists — let the agent re-decide

        var recoveredWithShift = plan.IsEstimated && !match.IsEstimated
            && (plan.LeaveAt - match.LeaveAt).Duration() > TimeSpan.FromMinutes(5);
        // LeaveAt matters as much as ArriveAt (29 Oct 2026, from real use): before departure
        // the arrival is pinned at start − buffer, so growing traffic moves leaveAt only —
        // an ArriveAt-only comparison left a 02:20 estimate on screen through the 07:40 rush.
        if (!recoveredWithShift
            && ((plan.ArriveAt - match.ArriveAt).Duration() >= TimeSpan.FromMinutes(1)
                || (plan.LeaveAt - match.LeaveAt).Duration() >= TimeSpan.FromMinutes(1)
                || plan.IsEstimated != match.IsEstimated))
        {
            // Why did leaveAt move? (replacement-nudge amendment, 29 Oct 2026) Same mode and
            // stops here, so only two causes exist: Traffic when the delay grew ≥3 min and
            // explains at least half the shift, otherwise Routing.
            if ((plan.LeaveAt - match.LeaveAt).Duration() >= TimeSpan.FromMinutes(1))
            {
                plan.ShiftReason = TrafficOrRouting(plan.TrafficDelayMin, match.TrafficDelayMin,
                    (plan.LeaveAt - match.LeaveAt).TotalMinutes);
                plan.ShiftedFromLeaveAt = plan.LeaveAt;
            }
            plan.LeaveAt = match.LeaveAt;
            plan.LeaveBy = match.LeaveBy;
            plan.ArriveAt = match.ArriveAt;
            plan.IsFeasible = match.IsFeasible;
            plan.SlackMin = match.SlackMin;
            plan.IsEstimated = match.IsEstimated;
            plan.TrafficDelayMin = match.TrafficDelayMin;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Plan v{Version} refreshed in place for {AppointmentId}: arrive {ArriveAt:HH:mm}{Estimated}",
                plan.Version, appointmentId, plan.ArriveAt, plan.IsEstimated ? " (estimated)" : "");
        }

        var best = result.Options.MinBy(o => o.ArriveAt)!;
        var betterAlternative = plan.ArriveAt - best.ArriveAt > TimeSpan.FromMinutes(5);
        return (plan, betterAlternative, recoveredWithShift);
    }

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

        // Chained origins (D6 amendment, 28 Oct 2026): only the NEXT placed appointment
        // departs from the current fix. A look-ahead appointment departs from the previous
        // placed appointment's place when the gap is at most Phase:ChainGapHours, else home.
        var now = clock.GetUtcNow();
        // Client-side date filtering: SQLite (tests) cannot compare DateTimeOffset in SQL.
        var placedOthers = (await db.Appointments.AsNoTracking().Include(a => a.Place)
                .Where(a => a.Id != appointment.Id && a.PlaceId != null && !a.IsVirtual)
                .ToListAsync(ct))
            .Where(a => a.Place is not null)
            .ToList();
        var isNext = appointment.Start <= now
            || !placedOthers.Any(a => a.Start > now && a.Start < appointment.Start);

        var current = await location.GetAsync(ct);
        GeoPoint origin;
        OriginKind originKind;
        string? originName = null;
        // The chain origin is the most recently STARTED placed appointment — overlapping
        // events (JO7 still running when JO9 starts) chain from where the user actually is,
        // not from the last one that happened to end (live, 28 Oct).
        var chained = placedOthers
            .Where(a => a.Start < appointment.Start && appointment.Start - a.End <= TimeSpan.FromHours(phase.ChainGapHours))
            .MaxBy(a => a.Start);
        if (!isNext && chained is { } previous)
        {
            origin = new GeoPoint(previous.Place!.Lat, previous.Place.Lng);
            originKind = OriginKind.PreviousAppointment;
            originName = previous.Title;
        }
        else if (isNext && current is { IsStale: false })
        {
            origin = new GeoPoint(current.Point.Lat, current.Point.Lng);
            originKind = OriginKind.Current;
        }
        else if (homePlace is not null)
        {
            origin = new GeoPoint(homePlace.Lat, homePlace.Lng); // stale/no location → home (location-ingest spec)
            originKind = OriginKind.Home;
        }
        else if (current is not null)
        {
            origin = new GeoPoint(current.Point.Lat, current.Point.Lng); // stale but better than nothing
            originKind = OriginKind.Current;
        }
        else
        {
            return (null, "no current location and no home place configured");
        }

        var inputs = await db.PlanInputs.AsNoTracking().SingleOrDefaultAsync(p => p.AppointmentId == appointmentId, ct);
        var key = BuildPlanningContext(appointment, settings, homePlace, origin);
        return (new PlanContext(appointment, settings, homePlace, origin, inputs, key, originKind, originName), null);
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

    /// <summary>Traffic when the traffic delay grew by ≥3 min AND that growth explains at
    /// least half of the leaveAt shift; otherwise Routing (replacement-nudge amendment).</summary>
    private static PlanShiftReason TrafficOrRouting(int oldDelayMin, int newDelayMin, double shiftMin)
    {
        var grewBy = newDelayMin - oldDelayMin;
        return grewBy >= 3 && grewBy >= Math.Abs(shiftMin) / 2 ? PlanShiftReason.Traffic : PlanShiftReason.Routing;
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
