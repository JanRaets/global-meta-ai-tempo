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
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public DateTimeOffset? LastProposedAt { get; set; }
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
    public long? TelegramChatId { get; set; }
}
