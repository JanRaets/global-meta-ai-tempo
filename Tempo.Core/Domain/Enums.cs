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
}

/// <summary>Where a plan's departure is measured from (chained origins, D6 amendment).</summary>
public enum OriginKind
{
    Current,
    PreviousAppointment,
    Home,
}
