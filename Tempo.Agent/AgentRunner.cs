using System.Diagnostics;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Models;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;

namespace Tempo.Agent;

/// <summary>
/// One agent run (design D7): a Muse Spark tool-calling loop over the deterministic tools.
/// Each run gets its own DI scope, so plan.commit can only use optionIds from this run's
/// latest plan.options. Ends on a final message or after 8 tool calls (trace: Truncated).
/// </summary>
public sealed class AgentRunner(
    IServiceScopeFactory scopeFactory,
    ILogger<AgentRunner> logger,
    Tempo.Core.Configuration.AgentOptions? options = null)
{
    public const int MaxToolCalls = 8;
    private readonly Tempo.Core.Configuration.AgentOptions limits = options ?? new();
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> SystemPrompt = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "agent.system.md")));

    public Task<AgentTrace?> RunAsync(string trigger, Guid? appointmentId, CancellationToken ct = default) =>
        RunAsync(trigger, appointmentId, note: null, ct);

    /// <summary>`note` is a deterministic one-liner from the caller recorded on the trace
    /// and handed to the model as context — e.g. "replan: traffic +7 min" (29 Oct 2026).</summary>
    public async Task<AgentTrace?> RunAsync(string trigger, Guid? appointmentId, string? note, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<TempoDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var models = sp.GetRequiredService<IMetaModels>();
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var now = clock.GetUtcNow();

        // 13.c: hard daily cap against a runaway loop burning credits. Counted client-side
        // (SQLite in tests can't compare DateTimeOffset); the traces table stays small.
        var dayStart = new DateTimeOffset(TimeZoneInfo.ConvertTime(now, tz).Date, TimeZoneInfo.ConvertTime(now, tz).Offset)
            .ToUniversalTime();
        var runsToday = (await db.AgentTraces.AsNoTracking()
                .Where(t => t.Trigger != "phase") // phase transitions are bookkeeping, not model runs
                .Select(t => t.StartedAt).ToListAsync(ct))
            .Count(t => t >= dayStart);
        if (runsToday >= limits.MaxRunsPerDay)
        {
            logger.LogError(
                "DAILY AGENT-RUN CAP HIT ({Cap}): run '{Trigger}' refused — no model calls until local midnight. " +
                "Raise Agent:MaxRunsPerDay only if this is expected.", limits.MaxRunsPerDay, trigger);
            return null;
        }

        var appointment = appointmentId is { } id
            ? await db.Appointments.AsNoTracking().Include(a => a.Place).SingleOrDefaultAsync(a => a.Id == id, ct)
            : (await db.Appointments.AsNoTracking().Include(a => a.Place).ToListAsync(ct))
                .Where(a => a.PlaceId != null && !a.IsVirtual && a.Start > now && a.Start <= now.AddHours(6))
                .OrderBy(a => a.Start)
                .FirstOrDefault();
        if (appointment is null)
        {
            logger.LogInformation("Agent run skipped ({Trigger}): no plannable appointment", trigger);
            return null;
        }

        var trace = new AgentTrace { Id = Guid.NewGuid(), Trigger = trigger, StartedAt = now };
        if (note is not null)
            trace.Steps.Add(new TraceStep(0, "note", "{}", note)); // "replan: traffic +7 min"
        db.AgentTraces.Add(trace);
        await db.SaveChangesAsync(ct);

        var executor = new AgentToolExecutor(
            db,
            sp.GetRequiredService<PlanService>(),
            sp.GetRequiredService<CurrentLocationProvider>(),
            sp.GetRequiredService<NudgeService>(),
            clock, settings, trace.Id)
        { RunAppointmentId = appointment.Id };

        var currentPlan = await sp.GetRequiredService<PlanService>().CurrentAsync(appointment.Id, ct);
        string Fmt(DateTimeOffset t) => TimeZoneInfo.ConvertTime(t, tz).ToString("HH:mm");
        var context = new
        {
            trigger,
            now = Fmt(now),
            date = TimeZoneInfo.ConvertTime(now, tz).ToString("yyyy-MM-dd ddd"),
            nudgeLeadMin = settings.NudgeLeadMin,
            appointment = new
            {
                id = appointment.Id,
                title = appointment.Title,
                start = Fmt(appointment.Start),
                place = appointment.Place!.Name,
            },
            currentPlan = currentPlan is null ? null : new
            {
                version = currentPlan.Version,
                mode = currentPlan.Mode.ToString(),
                leaveAt = Fmt(currentPlan.LeaveAt),
                stops = currentPlan.Stops.Select(s => s.Name),
                isEstimated = currentPlan.IsEstimated,
                // Why leaveAt last moved + the pre-shift time (replacement nudge, 29 Oct).
                shiftReason = currentPlan.ShiftReason.ToString(),
                shiftedFromLeaveAt = currentPlan.ShiftedFromLeaveAt is { } from ? Fmt(from) : null,
            },
            // Deterministic replan summary from the tick, e.g. "replan: traffic +7 min".
            replan = note,
            // The buffer is a margin (D6, 28 Oct): past leaveBy is late; past leaveAt is
            // only "leaveNow" — leaving immediately still arrives on time.
            state = currentPlan is null ? "normal"
                : now > currentPlan.LeaveBy ? "late"
                : now > currentPlan.LeaveAt ? "leaveNow"
                : "normal",
        };

        var messages = new List<ChatMessage>
        {
            ChatMessage.System(SystemPrompt.Value),
            ChatMessage.User(JsonSerializer.Serialize(context, Json)),
        };

        var stopwatch = Stopwatch.StartNew();
        var calls = 0;
        try
        {
            while (true)
            {
                var turn = await models.ChatAsync(messages, AgentToolDefinitions.All,
                    new ChatCallOptions { Purpose = "agent-run" }, ct);
                trace.Provider = turn.Provider;
                if (turn.IsFinal)
                {
                    trace.FinalMessage = turn.Text;
                    trace.Outcome = executor.Committed ? TraceOutcome.Committed : TraceOutcome.NoAction;
                    break;
                }
                messages.Add(ChatMessage.Assistant(turn.Text ?? "", turn.ToolCalls));
                var truncated = false;
                foreach (var call in turn.ToolCalls)
                {
                    calls++;
                    var (resultJson, summary) = await executor.ExecuteAsync(call, ct);
                    trace.Steps.Add(new TraceStep(calls, call.Name, Truncate(call.ArgumentsJson, 200), Truncate(summary, 300)));
                    messages.Add(ChatMessage.ToolResult(call.Id, resultJson));
                    if (calls >= MaxToolCalls)
                    {
                        truncated = true;
                        break;
                    }
                }
                if (truncated)
                {
                    trace.Outcome = TraceOutcome.Truncated;
                    break;
                }
            }
        }
        catch (ModelUnavailableException ex)
        {
            trace.Outcome = TraceOutcome.Failed;
            trace.FinalMessage = $"model unavailable: {ex.Message}";
            logger.LogWarning("Agent run failed ({Trigger}): {Message}", trigger, ex.Message);
        }
        trace.DurationMs = stopwatch.ElapsedMilliseconds;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Agent run {Outcome} ({Trigger}): {Calls} tool call(s) in {Ms} ms",
            trace.Outcome, trigger, calls, trace.DurationMs);
        return trace;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
