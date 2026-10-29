using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;

namespace Tempo.Core.Planning;

/// <summary>Throttle state for the ingest-driven en-route refresh — in memory on purpose:
/// after a restart the first post simply refreshes once more.</summary>
public sealed class EnRouteEtaThrottle
{
    private readonly ConcurrentDictionary<Guid, (DateTimeOffset At, double Lat, double Lng)> last = new();

    public bool ShouldRefresh(Guid planId, DateTimeOffset now, double lat, double lng, int minSeconds, int minMeters)
    {
        if (!last.TryGetValue(planId, out var prev))
            return true;
        return (now - prev.At).TotalSeconds >= minSeconds
            || GeoMath.DistanceMeters(lat, lng, prev.Lat, prev.Lng) >= minMeters;
    }

    public void Record(Guid planId, DateTimeOffset now, double lat, double lng) =>
        last[planId] = (now, lat, lng);
}

/// <summary>Live ETA on departure (amended 29 Oct 2026, from real use: the screen said
/// "On your way · arrive 08:15" for minutes into a drive that arrived 08:26, because the
/// phase flip rides the ingest while the ETA refresh rode the 2-minute tick). When an
/// accepted location post flips a plan to EnRoute, the committed option is re-estimated in
/// place in the same request; while EnRoute, every further accepted post refreshes it
/// again, throttled to once per Phase:EnRouteRefreshSeconds or Phase:EnRouteRefreshMeters
/// moved. The tick's refresh stays as the fallback for sparse posts. Deterministic
/// throughout: no model call, no version bump, no nudge.</summary>
public sealed class EnRouteEtaService(
    TempoDbContext db,
    PlanService plans,
    CurrentLocationProvider location,
    EnRouteEtaThrottle throttle,
    PhaseOptions options,
    TimeProvider clock,
    ILogger<EnRouteEtaService> logger)
{
    public async Task RefreshOnIngestAsync(IReadOnlyList<PhaseTransition> transitions, CancellationToken ct = default)
    {
        var fix = await location.GetAsync(ct);
        if (fix is null || fix.IsStale)
            return;
        var now = clock.GetUtcNow();
        var flipped = transitions
            .Where(t => t.To == PlanPhase.EnRoute)
            .Select(t => t.AppointmentId)
            .ToHashSet();

        // Candidate appointments by phase, then the CURRENT plan per appointment — an old
        // version can keep a stale EnRoute phase after a mid-drive version bump.
        var candidates = await db.DeparturePlans.AsNoTracking()
            .Where(p => p.Phase == PlanPhase.EnRoute)
            .Select(p => p.AppointmentId)
            .Distinct()
            .ToListAsync(ct);

        foreach (var appointmentId in candidates.Union(flipped))
        {
            var plan = await plans.CurrentAsync(appointmentId, ct);
            if (plan is not { Phase: PlanPhase.EnRoute })
                continue;
            if (!flipped.Contains(appointmentId)
                && !throttle.ShouldRefresh(plan.Id, now, fix.Point.Lat, fix.Point.Lng,
                    options.EnRouteRefreshSeconds, options.EnRouteRefreshMeters))
                continue;
            await plans.RefreshCommittedAsync(appointmentId, ct);
            throttle.Record(plan.Id, now, fix.Point.Lat, fix.Point.Lng);
            logger.LogInformation("En-route ETA refreshed on ingest for {AppointmentId}{Note}",
                appointmentId, flipped.Contains(appointmentId) ? " (departure detected)" : "");
        }
    }
}
