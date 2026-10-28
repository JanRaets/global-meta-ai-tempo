using Tempo.Core.Domain;

namespace Tempo.Core.Nudging;

/// <summary>
/// Deterministic nudge wording for when the agent run ends without a valid send (nudging
/// spec: "after the run ends without a valid send, the system sends a deterministic
/// template"). Built only from the committed plan, so it always passes notify.send's
/// validation: leave/arrival time, mode word, at most two sentences, no foreign times.
/// </summary>
public static class NudgeTemplates
{
    public static string Build(NudgeKind kind, DeparturePlan plan, Appointment appointment, TimeZoneInfo tz)
    {
        string Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm");
        var mode = plan.Mode.ToString().ToLowerInvariant();
        var stops = plan.Stops.Count == 0 ? "" : $", with a stop at {string.Join(" and ", plan.Stops.Select(s => s.Name))}";
        var roughly = plan.IsEstimated ? "roughly " : ""; // D18: estimates say so
        return kind switch
        {
            NudgeKind.Late => $"Leave now by {mode} for {appointment.Title} — you'll arrive at {roughly}{Local(plan.ArriveAt)}.",
            // D6 (28 Oct): the gentle one at leaveAt — on time, no alarm, just go.
            NudgeKind.LeaveNow => $"Time to go — leave now by {mode} for {appointment.Title} and you're there at {roughly}{Local(plan.ArriveAt)}.",
            _ => $"Time to leave at {roughly}{Local(plan.LeaveAt)} by {mode} for {appointment.Title}{stops}.",
        };
    }
}
