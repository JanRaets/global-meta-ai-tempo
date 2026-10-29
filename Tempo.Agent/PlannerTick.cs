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
            // Near-departure refresh (29 Oct 2026, from real use: a plan computed at 02:20
            // still showed 16 min in the 07:40 rush while Google said 20): while Upcoming or
            // LeaveNow within 60 min of leaveAt, re-estimate the committed option in place on
            // every tick — fresh routing through the 5-min cache, same mode and stops, no
            // version bump, no model call, no nudge. The refresh runs BEFORE the nudge-window
            // check below, so a Leave nudge always carries the freshest leave time; switching
            // to a different option remains the agent's inputs-changed business.
            else if (plan is { Phase: PlanPhase.Upcoming or PlanPhase.LeaveNow }
                && now >= plan.LeaveAt.AddMinutes(-60))
            {
                var refreshed = await plans.RefreshCommittedAsync(appointment.Id, ct);
                if (refreshed is { } r)
                    plan = r.Plan;
            }

            // AtDestination (D6 amendment): the zero-travel plan commits deterministically —
            // no model run, no nudge. "already at destination" is the whole decision.
            if (plan is { Phase: PlanPhase.AtDestination } && (plan.LeaveAt != appointment.Start || plan.Stops.Count > 0))
            {
                var zeroOptions = await plans.OptionsAsync(appointment.Id, ct);
                var zero = zeroOptions.Options.FirstOrDefault(o => o.IsNoTravel);
                if (zero is not null)
                {
                    await plans.CommitAsync(zero.OptionId, "already at destination", PlanCommitter.Fallback, ct);
                    plan = await plans.CurrentAsync(appointment.Id, ct);
                }
            }

            var windowOpen = plan is { Phase: PlanPhase.Upcoming }
                && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && !await NudgeExistsAsync(plan, NudgeKind.Leave, ct);
            // Traffic-aware replacement (29 Oct 2026): the refresh above moved leaveAt more
            // than 5 min EARLIER after the Leave nudge already went out — one replacement per
            // appointment, inside the window, only while still Upcoming (past leaveAt the
            // LeaveNow/Late machinery owns the message). Before the window opens nothing is
            // sent: the countdown and reason line move silently.
            var replanOwed = plan is { Phase: PlanPhase.Upcoming, ShiftedFromLeaveAt: { } shiftedFrom }
                && (shiftedFrom - plan.LeaveAt).TotalMinutes > 5
                && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && await NudgeExistsAsync(plan, NudgeKind.Leave, ct)
                && !await db.Nudges.AnyAsync(n => n.AppointmentId == appointment.Id
                    && n.Kind == NudgeKind.Leave && n.ReplacesNudgeId != null
                    && n.Status != NudgeStatus.Cancelled, ct);
            var lateOpen = plan is { Phase: PlanPhase.Late }
                && !await NudgeExistsAsync(plan, NudgeKind.Late, ct);
            // D6 (28 Oct): at leaveAt one gentle leave-now nudge, while the buffer still
            // covers an on-time arrival; the late machinery starts only past leaveBy.
            var leaveNowOpen = plan is { Phase: PlanPhase.LeaveNow }
                && !await NudgeExistsAsync(plan, NudgeKind.LeaveNow, ct);

            // While EnRoute/Arrived the deterministic refresh above is the whole job: the
            // user's own movement changes the inputs every tick, and waking the model for
            // that burns runs and risks a mid-drive renudge (live, 28 Oct). The agent only
            // runs again for a genuinely better alternative or a routing recovery (D6).
            var inputsChanged = hash != state.Get(appointment.Id);
            var moving = plan is { Phase: PlanPhase.EnRoute or PlanPhase.Arrived or PlanPhase.AtDestination };
            var reason = routingRecovered ? "tick:routing-recovered"
                : betterAlternative ? "tick:late-replan"
                : moving ? null
                : inputsChanged ? "tick:inputs-changed"
                : lateOpen ? "tick:late"
                : leaveNowOpen ? "tick:leave-now"
                : replanOwed ? "tick:replan"
                : windowOpen ? "tick:nudge-window"
                : null;
            state.Set(appointment.Id, hash);
            if (reason is null)
                continue; // unchanged inputs, nothing pending → no model call

            // The trace records what moved and why: "replan: traffic +7 min".
            var note = reason == "tick:replan" && plan?.ShiftedFromLeaveAt is { } from
                ? $"replan: {plan.ShiftReason.ToString().ToLowerInvariant()} +{Math.Round((from - plan.LeaveAt).TotalMinutes)} min"
                : null;
            var trace = await runner.RunAsync(reason, appointment.Id, note, ct);
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
        if (plan is null || plan.Phase is PlanPhase.EnRoute or PlanPhase.Arrived or PlanPhase.AtDestination)
            return; // no nudges once moving or already there (D8, 13.e, D6 amendment)
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var now = clock.GetUtcNow();
        NudgeKind? owed =
            plan.Phase == PlanPhase.Late
                && !await NudgeExistsAsync(plan, NudgeKind.Late, ct) ? NudgeKind.Late
            : plan.Phase == PlanPhase.LeaveNow
                && !await NudgeExistsAsync(plan, NudgeKind.LeaveNow, ct) ? NudgeKind.LeaveNow
            : plan.Phase == PlanPhase.Upcoming && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
                && !await NudgeExistsAsync(plan, NudgeKind.Leave, ct) ? NudgeKind.Leave
            : null;
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        string? text = null;
        // Replacement owed (29 Oct 2026): the Leave nudge went out, then leaveAt moved more
        // than 5 min earlier — the deterministic template names the cause and both times;
        // notify.send enforces the once-per-appointment and earlier-only rules.
        if (owed is null
            && plan is { Phase: PlanPhase.Upcoming, ShiftedFromLeaveAt: { } shiftedFrom }
            && (shiftedFrom - plan.LeaveAt).TotalMinutes > 5
            && now >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin)
            && await NudgeExistsAsync(plan, NudgeKind.Leave, ct)
            && !await db.Nudges.AnyAsync(n => n.AppointmentId == appointment.Id
                && n.Kind == NudgeKind.Leave && n.ReplacesNudgeId != null
                && n.Status != NudgeStatus.Cancelled, ct))
        {
            owed = NudgeKind.Leave;
            text = Tempo.Core.Nudging.NudgeTemplates.BuildReplacement(plan, appointment, tz);
        }
        if (owed is null)
            return;
        text ??= Tempo.Core.Nudging.NudgeTemplates.Build(owed.Value, plan, appointment, tz);
        var result = await nudges.SendAsync(owed.Value, text, appointment.Id, traceId, ct);
        logger.LogInformation("Template fallback nudge ({Kind}): {Outcome}",
            owed, result.Sent ? "sent" : result.RefusalReason);
    }

    // A Late nudge is once per APPOINTMENT (28 Oct, live: a version bump re-sent it);
    // Leave and LeaveNow stay per plan version (a >5 min shift legitimately re-nudges).
    private Task<bool> NudgeExistsAsync(DeparturePlan plan, NudgeKind kind, CancellationToken ct) =>
        kind == NudgeKind.Late
            ? db.Nudges.AnyAsync(n => n.AppointmentId == plan.AppointmentId
                && n.Kind == kind && n.Status != NudgeStatus.Cancelled, ct)
            : db.Nudges.AnyAsync(n => n.PlanId == plan.Id && n.PlanVersion == plan.Version
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
