using Tempo.Core.Domain;
using Tempo.Core.Geo;

namespace Tempo.Core.Planning;

/// <summary>A computed departure option (design D6). Ephemeral: only the committed
/// DeparturePlan persists. The agent picks an optionId; it never edits the numbers.</summary>
public sealed record PlanOption(
    string OptionId,
    TravelMode Mode,
    IReadOnlyList<PlanStop> Stops,
    DateTimeOffset LeaveAt,
    DateTimeOffset ArriveAt,
    bool IsFeasible,
    int SlackMin,
    /// <summary>Built from the straight-line fallback, not real routing (design D18).</summary>
    bool IsEstimated = false,
    /// <summary>Chained origins (D6 amendment): where this departure is measured from.</summary>
    OriginKind OriginKind = OriginKind.Current,
    /// <summary>The previous appointment's title when OriginKind is PreviousAppointment.</summary>
    string? OriginName = null,
    /// <summary>The last moment that still arrives at start (start − travel − stops); LeaveAt
    /// is the buffered target. The buffer is a margin, not a deadline (D6, 28 Oct 2026).</summary>
    DateTimeOffset LeaveBy = default)
{
    /// <summary>Same-place plan: zero travel, no stops, leaveAt = start (D6 amendment).</summary>
    public bool IsNoTravel => LeaveAt == ArriveAt && Stops.Count == 0;
}
/// <summary>A stop as the planner routes it: place, dwell time and coordinates.</summary>
public sealed record PlannerStop(Guid PlaceId, string Name, int DurationMin, GeoPoint Point);

public sealed record PlanOptionsResult(IReadOnlyList<PlanOption> Options, string? Note = null)
{
    public static PlanOptionsResult Empty(string note) => new([], note);
}

public enum CommitStatus
{
    Committed,
    Unchanged,
    Rejected,
}

public sealed record CommitResult(CommitStatus Status, DeparturePlan? Plan, string? Error = null)
{
    public static CommitResult Committed(DeparturePlan plan) => new(CommitStatus.Committed, plan);
    public static CommitResult Unchanged(DeparturePlan plan) => new(CommitStatus.Unchanged, plan);
    public static CommitResult Rejected(string error) => new(CommitStatus.Rejected, null, error);
}

/// <summary>
/// Holds the options produced by the latest plan.options call, per appointment, within one
/// scope (one agent run). plan.commit accepts only ids found here — the model cannot commit
/// an option it did not just receive (design D1; departure-planning spec: Plan commit tool).
/// </summary>
public sealed class PlanOptionStore
{
    private readonly Dictionary<string, (Guid AppointmentId, PlanOption Option)> latest = [];

    public void ReplaceFor(Guid appointmentId, IReadOnlyList<PlanOption> options)
    {
        foreach (var stale in latest.Where(kv => kv.Value.AppointmentId == appointmentId).Select(kv => kv.Key).ToList())
            latest.Remove(stale);
        foreach (var option in options)
            latest[option.OptionId] = (appointmentId, option);
    }

    public (Guid AppointmentId, PlanOption Option)? TryGet(string optionId) =>
        latest.TryGetValue(optionId, out var entry) ? entry : null;

    /// <summary>Every option from the latest plan.options calls in this scope — the allowed
    /// time vocabulary for nudge text validation.</summary>
    public IReadOnlyCollection<(Guid AppointmentId, PlanOption Option)> All() => latest.Values;
}
