using Tempo.Core.Models;

namespace Tempo.Agent;

/// <summary>The agent's whole world: exactly the eight deterministic tools of the
/// agent-loop spec. Anything else returns an error result.</summary>
public static class AgentToolDefinitions
{
    public static readonly IReadOnlyList<ToolDefinition> All =
    [
        new("calendar.upcoming", "Upcoming appointments within the next N hours.",
            """{"type":"object","properties":{"hours":{"type":"integer","minimum":1,"maximum":24}},"required":["hours"]}"""),
        new("location.current", "Where the user is now: age of the fix, staleness, nearest known place.",
            """{"type":"object","properties":{}}"""),
        new("plan.options", "Compute departure options for an appointment: every enabled mode and stop subset.",
            """{"type":"object","properties":{"appointmentId":{"type":"string"}},"required":["appointmentId"]}"""),
        new("plan.commit", "Commit one option (by optionId from the latest plan.options) as the current plan, with a one-line reason.",
            """{"type":"object","properties":{"optionId":{"type":"string"},"reason":{"type":"string"}},"required":["optionId","reason"]}"""),
        new("plan.current", "The currently committed plan for an appointment, if any.",
            """{"type":"object","properties":{"appointmentId":{"type":"string"}},"required":["appointmentId"]}"""),
        new("habits.confirmed", "Confirmed habitual stops matching this appointment's context.",
            """{"type":"object","properties":{"appointmentId":{"type":"string"}},"required":["appointmentId"]}"""),
        new("notify.send", "Send one nudge to the user. kind is Leave or Late. The text is validated against the committed plan.",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["Leave","Late"]},"text":{"type":"string"}},"required":["kind","text"]}"""),
        new("trace.note", "Record a short remark in the run trace (not shown to the user as a nudge).",
            """{"type":"object","properties":{"text":{"type":"string"}},"required":["text"]}"""),
    ];
}
