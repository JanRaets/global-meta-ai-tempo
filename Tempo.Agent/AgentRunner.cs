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
public sealed class AgentRunner(IServiceScopeFactory scopeFactory, ILogger<AgentRunner> logger)
{
    public const int MaxToolCalls = 8;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static readonly Lazy<string> SystemPrompt = new(() =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Prompts", "agent.system.md")));

    public async Task<AgentTrace?> RunAsync(string trigger, Guid? appointmentId, CancellationToken ct = default)
    {
        using var scope = scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;
        var db = sp.GetRequiredService<TempoDbContext>();
        var clock = sp.GetRequiredService<TimeProvider>();
        var models = sp.GetRequiredService<IMetaModels>();
        var settings = await db.UserSettings.AsNoTracking().FirstAsync(ct);
        var tz = TimeZoneInfo.FindSystemTimeZoneById(settings.TimeZone);
        var now = clock.GetUtcNow();

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
            },
            state = currentPlan is not null && now > currentPlan.LeaveAt ? "late" : "normal",
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
