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
