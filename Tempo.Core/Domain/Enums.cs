namespace Tempo.Core.Domain;

public enum AppointmentSource
{
    Synced,
    Captured,
}

/// <summary>Train exists but is disabled by default in UserSettings (design D15); plan.options skips disabled modes.</summary>
public enum TravelMode
{
    Walking,
    Bicycle,
    Car,
    Train,
}

public enum HabitState
{
    Proposed,
    Confirmed,
    Rejected,
}

public enum NudgeKind
{
    Leave,
    Late,
    HabitProposal,
    /// <summary>The gentle "time to go" at leaveAt, while the buffer still covers an
    /// on-time arrival — one per plan version (D6 amendment, 28 Oct 2026).</summary>
    LeaveNow,
}

public enum NudgeStatus
{
    Pending,
    Sent,
    Failed,
    Cancelled,
}

public enum IntentType
{
    SkipStop,
    AddStop,
    RunningLate,
    LeavingNow,
    PinMode,
    ConfirmHabit,
    Unknown,
}

public enum TraceOutcome
{
    Committed,
    NoAction,
    Truncated,
    Failed,
}

public enum PlanCommitter
{
    Agent,
    Fallback,
}

/// <summary>Where the user is relative to the plan (design D6 phase model). Evaluated
/// deterministically on every tick and location post, never by the model.</summary>
public enum PlanPhase
{
    Upcoming,
    Late,
    EnRoute,
    Arrived,
    /// <summary>Already within the arrive radius of the destination before the appointment
    /// starts — zero travel, no stops, no nudges (D6 amendment, 28 Oct 2026).</summary>
    AtDestination,
    /// <summary>Between leaveAt (the buffered target) and leaveBy (the last moment that
    /// still arrives at start): leaving now is still on time (D6 amendment, 28 Oct 2026 —
    /// the buffer is a margin, not a deadline).</summary>
    LeaveNow,
}

/// <summary>Where a nudge came from (feed-hygiene amendment, 28 Oct 2026): the Recent feed
/// shows Live only; Demo nudges die with demo mode; Dev marks test residue.</summary>
public enum NudgeSource
{
    Live,
    Demo,
    Dev,
}

/// <summary>What actually happened on a past appointment's trip (outcome amendment,
/// 28 Oct 2026): derived once, stored on the plan, never recomputed.</summary>
public enum PlanOutcomeKind
{
    None,
    OnTime,
    Late,
    NoData,
}

/// <summary>Where a plan's departure is measured from (chained origins, D6 amendment).</summary>
public enum OriginKind
{
    Current,
    PreviousAppointment,
    Home,
}
