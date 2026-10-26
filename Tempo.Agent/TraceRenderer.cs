using Tempo.Core.Domain;

namespace Tempo.Agent;

/// <summary>Renders an AgentTrace as short plain-language lines — never JSON (design D12/D13).</summary>
public static class TraceRenderer
{
    public static IReadOnlyList<string> Render(AgentTrace trace)
    {
        var lines = new List<string>();
        foreach (var step in trace.Steps.OrderBy(s => s.Seq))
            lines.Add(Sentence(step));
        if (!string.IsNullOrWhiteSpace(trace.FinalMessage))
            lines.Add(trace.FinalMessage!);
        if (trace.Outcome == TraceOutcome.Truncated)
            lines.Add("Stopped after the tool-call limit.");
        return lines;
    }

    private static string Sentence(TraceStep step) => step.Tool switch
    {
        "calendar.upcoming" => $"Checked the calendar — {step.ResultSummary}.",
        "location.current" => $"Checked where you are — {step.ResultSummary}.",
        "plan.options" => $"Worked out the choices — {step.ResultSummary}.",
        "plan.commit" => $"Decided — {step.ResultSummary}.",
        "plan.current" => $"Looked at the current plan — {step.ResultSummary}.",
        "habits.confirmed" => $"Checked your usual stops — {step.ResultSummary}.",
        "notify.send" => $"Messaged you — {step.ResultSummary}.",
        "trace.note" => $"Noted: {step.ResultSummary}",
        _ => $"{step.Tool} — {step.ResultSummary}.",
    };
}
