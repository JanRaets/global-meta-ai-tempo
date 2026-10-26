using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;
using Tempo.Core.Planning;

namespace Tempo.Agent;

/// <summary>Last seen inputs hash per appointment. In memory on purpose: a restart causes at
/// most one redundant agent run per appointment.</summary>
public sealed class TickStateStore
{
    private readonly ConcurrentDictionary<Guid, string> hashes = new();

    public string? Get(Guid appointmentId) => hashes.TryGetValue(appointmentId, out var h) ? h : null;

    public void Set(Guid appointmentId, string hash) => hashes[appointmentId] = hash;
}

/// <summary>
/// The 2-minute planner tick (design D7, departure-planning spec): for each plannable
/// appointment in the next 6 hours, invoke the agent only when the inputs hash changed,
/// the committed plan's nudge window has opened, or the late state has been entered.
/// A failed run (model unavailable) falls back to the deterministic commit.
/// </summary>
public sealed class PlannerTickService(
    TempoDbContext db,
    AgentRunner runner,
    PlanService plans,
    TickStateStore state,
    CurrentLocationProvider location,
    TimeProvider clock,
    ILogger<PlannerTickService> logger)
{
    public async Task TickAsync(CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var appointments = (await db.Appointments.AsNoTracking()
                .Include(a => a.Place)
                .Where(a => a.PlaceId != null && !a.IsVirtual)
                .ToListAsync(ct))
            .Where(a => a.Start > now && a.Start <= now.AddHours(6))
            .ToList();

        foreach (var appointment in appointments)
        {
            var hash = await ComputeInputsHashAsync(appointment, ct);
            var plan = await plans.CurrentAsync(appointment.Id, ct);
            var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);

            var windowOpen = plan is not null
                && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && !await NudgeExistsAsync(plan, NudgeKind.Leave, ct);
            var lateOpen = plan is not null
                && now > plan.LeaveAt
                && !await AtDestinationAsync(appointment, ct)
                && !await NudgeExistsAsync(plan, NudgeKind.Late, ct);

            var reason = hash != state.Get(appointment.Id) ? "tick:inputs-changed"
                : lateOpen ? "tick:late"
                : windowOpen ? "tick:nudge-window"
                : null;
            state.Set(appointment.Id, hash);
            if (reason is null)
                continue; // unchanged inputs, nothing pending → no model call

            var trace = await runner.RunAsync(reason, appointment.Id, ct);
            if (trace?.Outcome == TraceOutcome.Failed)
            {
                var fallback = await plans.FallbackCommitAsync(appointment.Id, ct);
                logger.LogInformation("Deterministic fallback for {Appointment}: {Result}",
                    appointment.Title, fallback is null ? "no options" : $"v{fallback.Version} committed");
            }
        }
    }

    private Task<bool> NudgeExistsAsync(DeparturePlan plan, NudgeKind kind, CancellationToken ct) =>
        db.Nudges.AnyAsync(n => n.PlanId == plan.Id && n.PlanVersion == plan.Version
            && n.Kind == kind && n.Status != NudgeStatus.Cancelled, ct);

    private async Task<bool> AtDestinationAsync(Appointment appointment, CancellationToken ct)
    {
        var current = await location.GetAsync(ct);
        return current is { IsStale: false }
            && appointment.Place is not null
            && GeoMath.DistanceMeters(current.Point.Lat, current.Point.Lng, appointment.Place.Lat, appointment.Place.Lng) <= 150;
    }

    /// <summary>Everything that should wake the agent when it changes: the appointment itself,
    /// rounded location, the confirmed habit set, per-plan inputs and nudge-relevant settings.</summary>
    private async Task<string> ComputeInputsHashAsync(Appointment appointment, CancellationToken ct)
    {
        var current = await location.GetAsync(ct);
        var locationPart = current is { IsStale: false }
            ? $"{Math.Round(current.Point.Lat, 3)},{Math.Round(current.Point.Lng, 3)}"
            : "stale";
        var habitIds = await db.Habits.AsNoTracking()
            .Where(h => h.State == HabitState.Confirmed)
            .Select(h => h.Id)
            .OrderBy(h => h)
            .ToListAsync(ct);
        var inputs = await db.PlanInputs.AsNoTracking().SingleOrDefaultAsync(p => p.AppointmentId == appointment.Id, ct);
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);

        var payload = string.Join("|",
            appointment.Id, appointment.Start.UtcTicks, appointment.PlaceId,
            locationPart,
            string.Join(",", habitIds),
            inputs?.UpdatedAt.UtcTicks ?? 0,
            settings.NudgeLeadMin, string.Join(",", settings.EnabledModes), settings.PinnedMode,
            settings.QuietHoursStart, settings.QuietHoursEnd);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>Hangfire entry points.</summary>
public sealed class PlannerTickJob(PlannerTickService tick)
{
    public Task RunAsync(CancellationToken ct) => tick.TickAsync(ct);
}

public sealed class AgentRunJob(AgentRunner runner)
{
    public Task RunAsync(string trigger, Guid? appointmentId, CancellationToken ct) =>
        runner.RunAsync(trigger, appointmentId, ct);
}
