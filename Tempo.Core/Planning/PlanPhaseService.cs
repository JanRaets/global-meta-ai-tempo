using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Core.Planning;

public sealed record PhaseTransition(Guid AppointmentId, string AppointmentTitle, PlanPhase From, PlanPhase To, string Detail);

/// <summary>
/// Deterministic plan-phase transitions (design D6 phase model, 13.e). Runs on every
/// planner tick and on every accepted location post — never involves the model:
///   Upcoming → Late     when now passes leaveAt (snapshots the leave reference: fix or home)
///   any      → EnRoute  when, past leaveAt, the user moved > Phase:EnRouteMeters from the
///                       leave reference (never earlier: before leaveAt the reference would be
///                       home, and ordinary morning drift must not read as a departure)
///   any      → Arrived  within Phase:ArrivedMeters of the destination, or start + fallback
/// Each transition is recorded as a plain-language trace row (trigger "phase"), which the
/// daily run cap ignores.
/// </summary>
public sealed class PlanPhaseService(
    TempoDbContext db,
    CurrentLocationProvider location,
    PhaseOptions options,
    TimeProvider clock,
    ILogger<PlanPhaseService> logger)
{
    public async Task<IReadOnlyList<PhaseTransition>> EvaluateAllAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var transitions = new List<PhaseTransition>();
        var settings = await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(ct);
        var home = settings?.HomePlaceId is { } homeId
            ? await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == homeId, ct)
            : null;
        var fix = await location.GetAsync(ct);
        var fresh = fix is { IsStale: false } ? new GeoPoint(fix.Point.Lat, fix.Point.Lng) : null;

        // Plans still in play: appointment not longer ago than the arrived fallback.
        var appointments = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => a.PlaceId != null && !a.IsVirtual
                && a.Start > now.AddMinutes(-options.ArrivedFallbackMin - 60) && a.Start <= now.AddHours(6))
            .ToDictionary(a => a.Id);
        if (appointments.Count == 0)
            return transitions;

        var plans = (await db.DeparturePlans
                .Where(p => appointments.Keys.Contains(p.AppointmentId)).ToListAsync(ct))
            .GroupBy(p => p.AppointmentId)
            .Select(g => g.OrderByDescending(p => p.Version).First())
            .Where(p => p.Phase != PlanPhase.Arrived)
            .ToList();

        foreach (var plan in plans)
        {
            var appointment = appointments[plan.AppointmentId];
            var from = plan.Phase;

            // Snapshot the leave reference the first time we evaluate past leaveAt.
            if (now >= plan.LeaveAt && plan.LeaveFixLat is null)
            {
                var reference = fresh ?? (home is null ? null : new GeoPoint(home.Lat, home.Lng));
                if (reference is not null)
                    (plan.LeaveFixLat, plan.LeaveFixLng) = (reference.Lat, reference.Lng);
            }

            var to = NextPhase(plan, appointment, fresh, home, now, out var detail);
            if (to == from)
                continue;
            plan.Phase = to;
            plan.PhaseChangedAt = now;
            transitions.Add(new PhaseTransition(appointment.Id, appointment.Title, from, to, detail));
        }

        if (transitions.Count > 0)
        {
            foreach (var t in transitions)
            {
                db.AgentTraces.Add(new AgentTrace
                {
                    Id = Guid.NewGuid(),
                    Trigger = "phase",
                    StartedAt = now,
                    Outcome = TraceOutcome.NoAction,
                    FinalMessage = $"{t.AppointmentTitle}: {t.From} → {t.To}",
                    Steps = [new TraceStep(1, "phase", "{}", t.Detail)],
                });
                logger.LogInformation("Plan phase {From} → {To} for '{Title}': {Detail}",
                    t.From, t.To, t.AppointmentTitle, t.Detail);
            }
            await db.SaveChangesAsync(ct);
        }
        else if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct); // a snapshotted leave reference without a transition
        }
        return transitions;
    }

    private PlanPhase NextPhase(DeparturePlan plan, Appointment appointment, GeoPoint? fresh, Place? home,
        DateTimeOffset now, out string detail)
    {
        var withinDestination = fresh is not null && appointment.Place is not null
            && GeoMath.DistanceMeters(fresh.Lat, fresh.Lng, appointment.Place.Lat, appointment.Place.Lng) <= options.ArrivedMeters;

        // Arrived wins over everything — except that being there BEFORE the start, without
        // a journey, is AtDestination (D6 amendment, 28 Oct 2026): zero travel, no nudges.
        if (withinDestination && (plan.Phase == PlanPhase.EnRoute || now >= appointment.Start))
        {
            detail = $"Within {options.ArrivedMeters} m of {appointment.Place!.Name} — made it.";
            return PlanPhase.Arrived;
        }
        if (now >= appointment.Start.AddMinutes(options.ArrivedFallbackMin))
        {
            detail = $"The appointment started {options.ArrivedFallbackMin} min ago — assuming arrival.";
            return PlanPhase.Arrived;
        }
        if (withinDestination)
        {
            detail = $"Already within {options.ArrivedMeters} m of {appointment.Place!.Name} — no travel needed.";
            return PlanPhase.AtDestination;
        }
        if (plan.Phase == PlanPhase.AtDestination)
        {
            if (fresh is not null)
            {
                detail = "Moved away from the destination before the start — planning resumes.";
                return PlanPhase.Upcoming;
            }
            detail = ""; // no fresh evidence → stay put
            return PlanPhase.AtDestination;
        }

        // En route: moved away from where they were when it was time to leave (or from home).
        // Only meaningful once leaveAt has passed — before that there is no leave reference.
        var reference = plan.LeaveFixLat is { } lat && plan.LeaveFixLng is { } lng
            ? new GeoPoint(lat, lng)
            : home is null ? null : new GeoPoint(home.Lat, home.Lng);
        if (now >= plan.LeaveAt && plan.Phase != PlanPhase.EnRoute && fresh is not null && reference is not null)
        {
            var moved = GeoMath.DistanceMeters(fresh.Lat, fresh.Lng, reference.Lat, reference.Lng);
            if (moved > options.EnRouteMeters)
            {
                detail = $"Moved {Math.Round(moved)} m from the leave point — on the way.";
                return PlanPhase.EnRoute;
            }
        }
        if (plan.Phase == PlanPhase.EnRoute)
        {
            detail = "";
            return PlanPhase.EnRoute;
        }

        if (now > plan.LeaveAt)
        {
            detail = $"Past the planned leave time ({plan.LeaveAt:HH:mm} UTC) without moving.";
            return PlanPhase.Late;
        }
        detail = "";
        return PlanPhase.Upcoming;
    }
}
