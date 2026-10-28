using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Core.Planning;

/// <summary>
/// Outcome amendment (28 Oct 2026): for a past appointment with a place, derive once what
/// actually happened — leftAt (the EnRoute transition trace, else the first fix beyond the
/// en-route radius after leaveAt − 15 min), arrivedAt (the by-proximity Arrived trace, else
/// the Visit at the destination overlapping the appointment), and the outcome. Stored on
/// the plan; never recomputed, and no Visits are created here.
/// </summary>
public sealed class PlanOutcomeService(
    TempoDbContext db,
    ILocationBuffer buffer,
    PhaseOptions options,
    TimeProvider clock,
    ILogger<PlanOutcomeService> logger)
{
    /// <summary>Fills the plan's outcome when the appointment is over and none is stored.
    /// Saves its own changes. Returns the plan (or null when there is nothing to derive).</summary>
    public async Task<DeparturePlan?> EnsureAsync(Appointment appointment, CancellationToken ct = default)
    {
        if (appointment.PlaceId is null || appointment.End > clock.GetUtcNow())
            return null;
        var plan = (await db.DeparturePlans.Where(p => p.AppointmentId == appointment.Id).ToListAsync(ct))
            .OrderByDescending(p => p.Version)
            .FirstOrDefault();
        if (plan is null || plan.OutcomeKind != PlanOutcomeKind.None)
            return plan;

        var searchFrom = plan.LeaveAt.AddMinutes(-15);
        var searchTo = appointment.End.AddMinutes(options.ArrivedFallbackMin);
        var phaseTraces = (await db.AgentTraces.AsNoTracking().Where(t => t.Trigger == "phase").ToListAsync(ct))
            .Where(t => t.StartedAt >= searchFrom && t.StartedAt <= searchTo
                && t.FinalMessage is not null && t.FinalMessage.StartsWith($"{appointment.Title}:", StringComparison.Ordinal))
            .OrderBy(t => t.StartedAt)
            .ToList();

        var leftAt = phaseTraces.FirstOrDefault(t => t.FinalMessage!.EndsWith("→ EnRoute", StringComparison.Ordinal))?.StartedAt
            ?? await FirstMovedFixAsync(plan, searchFrom, ct);

        // Only a by-proximity arrival counts as evidence — the start+15 fallback is an
        // assumption, not data (its step detail says "assuming arrival").
        var arrivedAt = phaseTraces
            .FirstOrDefault(t => t.FinalMessage!.EndsWith("→ Arrived", StringComparison.Ordinal)
                && t.Steps.Any(s => s.ResultSummary.Contains("Within", StringComparison.Ordinal)))?.StartedAt;
        if (arrivedAt is null)
        {
            var visit = (await db.Visits.AsNoTracking().Where(v => v.PlaceId == appointment.PlaceId).ToListAsync(ct))
                .FirstOrDefault(v => v.ArrivedAt <= appointment.End && v.DepartedAt >= appointment.Start);
            arrivedAt = visit?.ArrivedAt;
        }

        plan.LeftAt = leftAt;
        plan.ActualArriveAt = arrivedAt;
        if (arrivedAt is { } arrived)
        {
            var lateMin = (int)Math.Ceiling((arrived - appointment.Start).TotalMinutes);
            (plan.OutcomeKind, plan.OutcomeLateMin) = lateMin > 0
                ? (PlanOutcomeKind.Late, (int?)lateMin)
                : (PlanOutcomeKind.OnTime, null);
        }
        else
        {
            plan.OutcomeKind = PlanOutcomeKind.NoData;
        }
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Outcome for '{Title}': {Outcome}{Late} (left {Left}, arrived {Arrived})",
            appointment.Title, plan.OutcomeKind, plan.OutcomeLateMin is { } n ? $" {n} min" : "",
            leftAt?.ToString("HH:mm") ?? "—", arrivedAt?.ToString("HH:mm") ?? "—");
        return plan;
    }

    /// <summary>The first buffered fix beyond the en-route radius from the leave reference
    /// (or home) after the search start — evidence of departure when no trace captured it.</summary>
    private async Task<DateTimeOffset?> FirstMovedFixAsync(DeparturePlan plan, DateTimeOffset from, CancellationToken ct)
    {
        GeoPoint? reference = null;
        if (plan is { LeaveFixLat: { } lat, LeaveFixLng: { } lng })
            reference = new GeoPoint(lat, lng);
        else
        {
            var homeId = await db.UserSettings.AsNoTracking().Select(s => s.HomePlaceId).FirstOrDefaultAsync(ct);
            var home = homeId is null ? null : await db.Places.AsNoTracking().SingleOrDefaultAsync(p => p.Id == homeId, ct);
            if (home is not null)
                reference = new GeoPoint(home.Lat, home.Lng);
        }
        if (reference is null)
            return null;
        return (await buffer.GetSinceAsync(from, ct))
            .FirstOrDefault(p => GeoMath.DistanceMeters(p.Lat, p.Lng, reference.Lat, reference.Lng) > options.EnRouteMeters)
            ?.Timestamp;
    }
}
