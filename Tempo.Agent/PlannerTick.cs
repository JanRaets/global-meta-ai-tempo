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
    PlanPhaseService phases,
    Tempo.Core.Nudging.NudgeService nudges,
    TickStateStore state,
    CurrentLocationProvider location,
    TimeProvider clock,
    ILogger<PlannerTickService> logger)
{
    public async Task TickAsync(CancellationToken ct = default)
    {
        await phases.EvaluateAllAsync(ct); // deterministic phase transitions first (D6)

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

            // 13.e bug 1 + D18 recovery: while Late/EnRoute — or while the plan is only an
            // estimate — keep the committed option's times honest via a deterministic
            // in-place refresh: no model call, no version bump, no nudge.
            var betterAlternative = false;
            var routingRecovered = false;
            if (plan is { Phase: PlanPhase.Late or PlanPhase.EnRoute } or { IsEstimated: true })
            {
                var refreshed = await plans.RefreshCommittedAsync(appointment.Id, ct);
                if (refreshed is { } r)
                    (plan, betterAlternative, routingRecovered) = (r.Plan, r.BetterAlternative, r.RecoveredWithShift);
            }

            var windowOpen = plan is { Phase: PlanPhase.Upcoming }
                && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && !await NudgeExistsAsync(plan, NudgeKind.Leave, ct);
            var lateOpen = plan is { Phase: PlanPhase.Late }
                && !await NudgeExistsAsync(plan, NudgeKind.Late, ct);

            var reason = hash != state.Get(appointment.Id) ? "tick:inputs-changed"
                : routingRecovered ? "tick:routing-recovered"
                : betterAlternative ? "tick:late-replan"
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
            // Nudging spec: "after the run ends without a valid send, the system sends a
            // deterministic template". The model had its chance this run; if a nudge is
            // still owed, send the template through the same guarded notify path.
            await SendTemplateIfOwedAsync(appointment, trace?.Id, ct);
        }
    }

    private async Task SendTemplateIfOwedAsync(Appointment appointment, Guid? traceId, CancellationToken ct)
    {
        await phases.EvaluateAllAsync(ct); // a plan committed this run may already be late
        var plan = await plans.CurrentAsync(appointment.Id, ct);
        if (plan is null || plan.Phase is PlanPhase.EnRoute or PlanPhase.Arrived)
            return; // no nudges once moving (D8, 13.e)
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var now = clock.GetUtcNow();
        NudgeKind? owed =
            plan.Phase == PlanPhase.Late
                && !await NudgeExistsAsync(plan, NudgeKind.Late, ct) ? NudgeKind.Late
            : plan.Phase == PlanPhase.Upcoming && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && !await NudgeExistsAsync(plan, NudgeKind.Leave, ct) ? NudgeKind.Leave
            : null;
        if (owed is null)
            return;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var text = Tempo.Core.Nudging.NudgeTemplates.Build(owed.Value, plan, appointment, tz);
        var result = await nudges.SendAsync(owed.Value, text, appointment.Id, traceId, ct);
        logger.LogInformation("Template fallback nudge ({Kind}): {Outcome}",
            owed, result.Sent ? "sent" : result.RefusalReason);
    }

    private Task<bool> NudgeExistsAsync(DeparturePlan plan, NudgeKind kind, CancellationToken ct) =>
        db.Nudges.AnyAsync(n => n.PlanId == plan.Id && n.PlanVersion == plan.Version
            && n.Kind == kind && n.Status != NudgeStatus.Cancelled, ct);

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
