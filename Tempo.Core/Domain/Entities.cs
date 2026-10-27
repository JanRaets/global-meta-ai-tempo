using Tempo.Core.Configuration;

namespace Tempo.Core.Domain;

/// <summary>A geocoded point with a name; reused when a new point lands within 50 m (calendar-sync spec).</summary>
public sealed class Place
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public string? Category { get; set; }
    public double Lat { get; set; }
    public double Lng { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class Appointment
{
    public Guid Id { get; set; }
    public AppointmentSource Source { get; set; }
    /// <summary>Google event id for synced appointments; null for captured.</summary>
    public string? ExternalId { get; set; }
    public required string Title { get; set; }
    public DateTimeOffset Start { get; set; }
    public DateTimeOffset End { get; set; }
    public string? RawLocation { get; set; }
    public Guid? PlaceId { get; set; }
    public Place? Place { get; set; }
    public bool IsVirtual { get; set; }
    /// <summary>Minutes the user wants to be early (design D6: leaveAt = start − buffer − travel − stops − detour).</summary>
    public int BufferMin { get; set; } = 5;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>A time-bounded stay at a Place, derived from raw location points. Raw points never persist (design D4).</summary>
public sealed class Visit
{
    public Guid Id { get; set; }
    public Guid PlaceId { get; set; }
    public Place? Place { get; set; }
    public DateTimeOffset ArrivedAt { get; set; }
    /// <summary>Null while the stay is ongoing; updated by a later collapse run.</summary>
    public DateTimeOffset? DepartedAt { get; set; }
}

public sealed class Habit
{
    public Guid Id { get; set; }
    public Guid PlaceId { get; set; }
    public Place? Place { get; set; }
    public required ContextKey Context { get; set; }
    public HabitState State { get; set; } = HabitState.Proposed;
    public double AvgDurationMin { get; set; }
    public int VisitCount { get; set; }
    public string? ProposalText { get; set; }
    public string? Rationale { get; set; }
    /// <summary>A paused confirmed habit stays confirmed but the planner skips it (D12 Habits screen).</summary>
    public bool IsPaused { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? LastProposedAt { get; set; }
    /// <summary>Times this proposal was included in a daily batch; an unanswered proposal is re-sent once after 7 days.</summary>
    public int ProposalSentCount { get; set; }
}

public sealed record PlanStop(Guid PlaceId, string Name, int DurationMin);

/// <summary>The committed departure plan for one appointment; a new version on any input change (design D6).</summary>
public sealed class DeparturePlan
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public int Version { get; set; }
    public TravelMode Mode { get; set; }
    public DateTimeOffset LeaveAt { get; set; }
    public DateTimeOffset ArriveAt { get; set; }
    public bool IsFeasible { get; set; }
    public int SlackMin { get; set; }
    public List<PlanStop> Stops { get; set; } = [];
    public required string Reason { get; set; }
    public PlanCommitter CommittedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    /// <summary>Built from the straight-line fallback while routing was down (design D18);
    /// worded "roughly" everywhere and replaced by the next tick once routing answers.</summary>
    public bool IsEstimated { get; set; }
    /// <summary>Design D6 phase model; transitions are deterministic (PlanPhaseService).</summary>
    public PlanPhase Phase { get; set; } = PlanPhase.Upcoming;
    public DateTimeOffset? PhaseChangedAt { get; set; }
    /// <summary>The fix (or home) at the moment leaveAt passed — the en-route reference point.</summary>
    public double? LeaveFixLat { get; set; }
    public double? LeaveFixLng { get; set; }
}

/// <summary>An outbound message tied to a plan version. PlanId/AppointmentId are plain ids
/// (no FK) so cancelled nudges survive plan and appointment deletion as history.</summary>
public sealed class Nudge
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public Guid PlanId { get; set; }
    public int PlanVersion { get; set; }
    public NudgeKind Kind { get; set; }
    public required string Text { get; set; }
    public NudgeStatus Status { get; set; } = NudgeStatus.Pending;
    public Guid? AgentTraceId { get; set; }
    /// <summary>Set when this nudge supersedes an earlier sent one for the same appointment
    /// and kind (plan version moved); the notification then presents itself as an update.</summary>
    public Guid? ReplacesNudgeId { get; set; }
    /// <summary>For a single-proposal HabitProposal nudge: the habit the Android
    /// notification's Confirm/Reject actions act on (design D16).</summary>
    public Guid? HabitId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>A user utterance (voice or text) turned into a structured plan adjustment.</summary>
public sealed class Intent
{
    public Guid Id { get; set; }
    public IntentType Type { get; set; }
    public Guid? AppointmentId { get; set; }
    public string? Transcript { get; set; }
    /// <summary>Typed arguments as JSON (placeId, placeName, minutes, mode — whichever apply).</summary>
    public string ArgsJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? UndoneAt { get; set; }
}

public sealed record ExtraStop(Guid PlaceId, string Name, int DurationMin);

/// <summary>Per-appointment planner input changes from voice intents (skip a stop, add a
/// stop, running late, pin a mode) — scoped to the current appointment only, never to
/// habits or the calendar (voice-input spec: Apply intent).</summary>
public sealed class PlanInputs
{
    public Guid Id { get; set; }
    public Guid AppointmentId { get; set; }
    public List<Guid> SkippedPlaceIds { get; set; } = [];
    public List<ExtraStop> ExtraStops { get; set; } = [];
    public int LateOffsetMin { get; set; }
    public TravelMode? PinnedMode { get; set; }
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed record TraceStep(int Seq, string Tool, string ArgsJson, string ResultSummary);

/// <summary>The recorded tool-call sequence and decision of one agent run (design D7).</summary>
public sealed class AgentTrace
{
    public Guid Id { get; set; }
    public required string Trigger { get; set; }
    public ModelProvider? Provider { get; set; }
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.UtcNow;
    public long DurationMs { get; set; }
    public TraceOutcome Outcome { get; set; }
    public string? FinalMessage { get; set; }
    public List<TraceStep> Steps { get; set; } = [];
}

/// <summary>Single row (one user, one phone).</summary>
public sealed class UserSettings
{
    public Guid Id { get; set; }
    public Guid? HomePlaceId { get; set; }
    public List<TravelMode> EnabledModes { get; set; } = [TravelMode.Walking, TravelMode.Bicycle, TravelMode.Car];
    public TravelMode? PinnedMode { get; set; }
    public int NudgeLeadMin { get; set; } = 10;
    public TimeOnly QuietHoursStart { get; set; } = new(22, 0);
    public TimeOnly QuietHoursEnd { get; set; } = new(7, 0);
    public bool DemoMode { get; set; }
    /// <summary>Per-user API key for POST /location (location-ingest spec); generated at first startup.</summary>
    public required string LocationApiKey { get; set; }
    public string TimeZone { get; set; } = "Europe/Amsterdam";
}

/// <summary>One browser push endpoint of the installed PWA (design D16). The endpoint plus
/// the client keys is everything the push service needs; 410/404 from it deletes the row.</summary>
public sealed class PushSubscription
{
    public Guid Id { get; set; }
    public required string Endpoint { get; set; }
    public required string P256dh { get; set; }
    public required string Auth { get; set; }
    public string? UserAgent { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
