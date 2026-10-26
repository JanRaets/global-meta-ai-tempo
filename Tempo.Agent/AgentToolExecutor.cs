using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;
using Tempo.Core.Models;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;

namespace Tempo.Agent;

/// <summary>
/// Executes one run's tool calls against the real services. Every result the model sees is
/// compact JSON; every step also yields a short human summary for the trace sheet.
/// Guards live in the services (plan.commit's same-run optionId check, notify.send's
/// refusals) — this class only dispatches and formats.
/// </summary>
public sealed class AgentToolExecutor(
    TempoDbContext db,
    PlanService plans,
    CurrentLocationProvider location,
    NudgeService nudges,
    TimeProvider clock,
    UserSettings settings,
    Guid traceId)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeZoneInfo tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);

    public bool Committed { get; private set; }
    public bool NudgeSent { get; private set; }

    public async Task<(string ResultJson, string Summary)> ExecuteAsync(ToolCall call, CancellationToken ct)
    {
        try
        {
            return call.Name switch
            {
                "calendar.upcoming" => await CalendarUpcomingAsync(call, ct),
                "location.current" => await LocationCurrentAsync(ct),
                "plan.options" => await PlanOptionsAsync(call, ct),
                "plan.commit" => await PlanCommitAsync(call, ct),
                "plan.current" => await PlanCurrentAsync(call, ct),
                "habits.confirmed" => await HabitsConfirmedAsync(call, ct),
                "notify.send" => await NotifySendAsync(call, ct),
                "trace.note" => TraceNote(call),
                _ => (Err($"unknown tool '{call.Name}'"), $"unknown tool '{call.Name}'"),
            };
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or ArgumentException)
        {
            return (Err($"bad arguments for {call.Name}: {ex.Message}"), $"bad arguments for {call.Name}");
        }
    }

    private async Task<(string, string)> CalendarUpcomingAsync(ToolCall call, CancellationToken ct)
    {
        var hours = Arg(call).TryGetProperty("hours", out var h) ? h.GetInt32() : 6;
        var now = clock.GetUtcNow();
        var upcoming = (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
            .Where(a => a.Start > now && a.Start <= now.AddHours(hours))
            .OrderBy(a => a.Start)
            .Select(a => new
            {
                id = a.Id,
                title = a.Title,
                date = Local(a.Start).ToString("yyyy-MM-dd"),
                start = Fmt(a.Start),
                place = a.Place?.Name,
                isVirtual = a.IsVirtual,
            })
            .ToList();
        return (ToJson(new { appointments = upcoming }), $"{upcoming.Count} upcoming appointment(s)");
    }

    private async Task<(string, string)> LocationCurrentAsync(CancellationToken ct)
    {
        var current = await location.GetAsync(ct);
        if (current is null)
            return (ToJson(new { known = false }), "no location fix");
        var near = (await db.Places.AsNoTracking().ToListAsync(ct))
            .Select(p => (p.Name, Distance: GeoMath.DistanceMeters(p.Lat, p.Lng, current.Point.Lat, current.Point.Lng)))
            .Where(x => x.Distance <= 150)
            .OrderBy(x => x.Distance)
            .Select(x => x.Name)
            .FirstOrDefault();
        var summary = near is null ? $"fix {current.AgeMinutes:F0} min old" : $"near {near}";
        return (ToJson(new { known = true, ageMinutes = Math.Round(current.AgeMinutes), isStale = current.IsStale, near }), summary);
    }

    private async Task<(string, string)> PlanOptionsAsync(ToolCall call, CancellationToken ct)
    {
        var result = await plans.OptionsAsync(AppointmentId(call), ct);
        var options = result.Options.Select(o => new
        {
            optionId = o.OptionId,
            mode = o.Mode.ToString().ToLowerInvariant(),
            stops = o.Stops.Select(s => s.Name).ToList(),
            leaveAt = Fmt(o.LeaveAt),
            arriveAt = Fmt(o.ArriveAt),
            isFeasible = o.IsFeasible,
            slackMin = o.SlackMin,
        }).ToList();
        return (ToJson(new { note = result.Note, options }),
            options.Count == 0 ? $"no options ({result.Note})" : $"computed {options.Count} option(s)");
    }

    private async Task<(string, string)> PlanCommitAsync(ToolCall call, CancellationToken ct)
    {
        var args = Arg(call);
        var optionId = args.GetProperty("optionId").GetString() ?? "";
        var reason = args.TryGetProperty("reason", out var r) ? r.GetString() ?? "" : "";
        var result = await plans.CommitAsync(optionId, reason, PlanCommitter.Agent, ct);
        if (result.Status == CommitStatus.Rejected)
            return (Err(result.Error!), $"commit refused: {result.Error}");
        Committed = true;
        var plan = result.Plan!;
        var stops = plan.Stops.Count == 0 ? "no stops" : string.Join(", ", plan.Stops.Select(s => s.Name));
        return (ToJson(new
        {
            committed = true,
            version = plan.Version,
            mode = plan.Mode.ToString().ToLowerInvariant(),
            leaveAt = Fmt(plan.LeaveAt),
            arriveAt = Fmt(plan.ArriveAt),
            nudgeWindowOpen = NudgeWindowOpen(plan), // computed here — the model never does clock arithmetic
        }),
            $"committed v{plan.Version}: {plan.Mode}, leave {Fmt(plan.LeaveAt)}, {stops}");
    }

    private async Task<(string, string)> PlanCurrentAsync(ToolCall call, CancellationToken ct)
    {
        var plan = await plans.CurrentAsync(AppointmentId(call), ct);
        if (plan is null)
            return (ToJson(new { none = true }), "no committed plan");
        return (ToJson(new
        {
            mode = plan.Mode.ToString().ToLowerInvariant(),
            leaveAt = Fmt(plan.LeaveAt),
            arriveAt = Fmt(plan.ArriveAt),
            stops = plan.Stops.Select(s => s.Name).ToList(),
            isFeasible = plan.IsFeasible,
            nudgeWindowOpen = NudgeWindowOpen(plan),
        }), $"current plan: {plan.Mode}, leave {Fmt(plan.LeaveAt)}");
    }

    private async Task<(string, string)> HabitsConfirmedAsync(ToolCall call, CancellationToken ct)
    {
        var habits = await plans.ConfirmedMatchingAsync(AppointmentId(call), ct);
        return (ToJson(new { habits = habits.Select(h => new { name = h.PlaceName, avgMin = Math.Round(h.AvgDurationMin) }) }),
            habits.Count == 0 ? "no matching habits" : $"{habits.Count} matching habit(s)");
    }

    private async Task<(string, string)> NotifySendAsync(ToolCall call, CancellationToken ct)
    {
        var args = Arg(call);
        var kindText = args.GetProperty("kind").GetString();
        var text = args.GetProperty("text").GetString() ?? "";
        if (!Enum.TryParse<NudgeKind>(kindText, ignoreCase: true, out var kind) || kind == NudgeKind.HabitProposal)
            return (Err($"kind must be Leave or Late, not '{kindText}'"), "notify refused: bad kind");
        var appointmentId = AppointmentForNudge();
        var result = await nudges.SendAsync(kind, text, appointmentId, traceId, ct);
        if (!result.Sent)
            return (Err(result.RefusalReason!), $"notify refused: {result.RefusalReason}");
        NudgeSent = true;
        return (ToJson(new { sent = true }), $"sent {kind} nudge: \"{text}\"");
    }

    private (string, string) TraceNote(ToolCall call)
    {
        var text = Arg(call).GetProperty("text").GetString() ?? "";
        return (ToJson(new { ok = true }), text);
    }

    /// <summary>The appointment this run is about; set by the runner before any tool call.</summary>
    public Guid RunAppointmentId { get; set; }

    private Guid AppointmentForNudge() => RunAppointmentId;

    private Guid AppointmentId(ToolCall call)
    {
        var raw = Arg(call).GetProperty("appointmentId").GetString() ?? "";
        return Guid.TryParse(raw, out var id) ? id : throw new ArgumentException($"'{raw}' is not an appointment id");
    }

    private bool NudgeWindowOpen(DeparturePlan plan) =>
        clock.GetUtcNow() >= plan.LeaveAt.AddMinutes(-settings.NudgeLeadMin);

    private static JsonElement Arg(ToolCall call) => JsonDocument.Parse(
        string.IsNullOrWhiteSpace(call.ArgumentsJson) ? "{}" : call.ArgumentsJson).RootElement;

    private DateTimeOffset Local(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz);

    private string Fmt(DateTimeOffset t) => Local(t).ToString("HH:mm");

    private static string ToJson(object o) => JsonSerializer.Serialize(o, Json);

    private static string Err(string message) => JsonSerializer.Serialize(new { error = message }, Json);
}
