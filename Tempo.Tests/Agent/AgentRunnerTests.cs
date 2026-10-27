using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Agent;
using Tempo.Core.Configuration;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Location;
using Tempo.Core.Models;
using Tempo.Core.Nudging;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;
using Tempo.Tests.Nudging;

namespace Tempo.Tests.Agent;

/// <summary>
/// Agent loop against real tools with a scripted model (agent-loop spec). World: appointment
/// 10:00 local (09:00Z), bike 18 min, buffer 5 → leaveAt 09:37 local; "now" varies per test.
/// </summary>
public sealed class AgentRunnerTests : IDisposable
{
    private static readonly DateTimeOffset AppointmentStart = DateTimeOffset.Parse("2026-10-27T09:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly ScriptedMetaModels model = new();
    private readonly RecordingNudgeTransport transport = new();
    private readonly ServiceProvider provider;
    private readonly AgentRunner runner;
    private readonly Appointment appointment;
    private readonly Place cafe;

    public AgentRunnerTests()
    {
        var destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        cafe = new Place { Id = Guid.NewGuid(), Name = "Bar Bello", Lat = 52.0905, Lng = 5.2000 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = AppointmentStart,
            End = AppointmentStart.AddHours(1),
            PlaceId = destination.Id,
            Place = destination,
            BufferMin = 5,
        };
        db.Context.AddRange(destination, cafe, appointment,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", EnabledModes = [TravelMode.Bicycle] });
        db.Context.SaveChanges();
        FreshLocation();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<TimeProvider>(clock);
        services.AddSingleton(db.Context);
        services.AddSingleton<IMetaModels>(model);
        services.AddSingleton<ILocationBuffer>(buffer);
        services.AddSingleton<CurrentLocationProvider>();
        services.AddSingleton<IRoutingClient>(routing);
        services.AddScoped<DeparturePlanner>();
        services.AddScoped<PlanOptionStore>();
        services.AddScoped<PlanService>();
        services.AddSingleton<INudgeTransport>(transport);
        services.AddScoped<NudgeService>();
        provider = services.BuildServiceProvider();
        runner = new AgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentRunner>.Instance);
    }

    private void FreshLocation() =>
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-2)));

    private static ToolCall Call(string name, object args, string id = "c") =>
        new(id + Guid.NewGuid().ToString("N")[..4], name, JsonSerializer.Serialize(args));

    private static JsonElement LastToolResult(IReadOnlyList<ChatMessage> messages) =>
        JsonDocument.Parse(messages.Last(m => m.Role == ChatRole.Tool).Content).RootElement;

    private static JsonElement FirstToolResult(IReadOnlyList<ChatMessage> messages) =>
        JsonDocument.Parse(messages.First(m => m.Role == ChatRole.Tool).Content).RootElement;

    private ToolCall CommitBest(IReadOnlyList<ChatMessage> messages, bool preferFeasible = true)
    {
        var options = LastToolResult(messages).GetProperty("options").EnumerateArray().ToList();
        var pick = preferFeasible
            ? options.FirstOrDefault(o => o.GetProperty("isFeasible").GetBoolean())
            : default;
        if (pick.ValueKind == JsonValueKind.Undefined)
            pick = options.OrderByDescending(o => o.GetProperty("slackMin").GetInt32()).First();
        return Call("plan.commit", new { optionId = pick.GetProperty("optionId").GetString(), reason = "scripted pick" });
    }

    [Fact]
    public async Task The_daily_run_cap_refuses_a_run_without_a_model_call()
    {
        var capped = new AgentRunner(provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AgentRunner>.Instance, new Tempo.Core.Configuration.AgentOptions { MaxRunsPerDay = 1 });
        db.Context.AgentTraces.Add(new AgentTrace { Id = Guid.NewGuid(), Trigger = "earlier", StartedAt = clock.GetUtcNow().AddHours(-1) });
        db.Context.SaveChanges();

        var trace = await capped.RunAsync("tick:inputs-changed", appointment.Id);

        Assert.Null(trace);
        Assert.Empty(model.ChatInvocations); // the cap blocks before any model call
    }

    [Fact]
    public async Task Normal_plan_commits_one_option_and_sends_no_nudge()
    {
        model
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueDynamic(m => new ChatResult(null, [CommitBest(m)], ModelProvider.MetaApi))
            .EnqueueFinal("Planned the dentist trip.");

        var trace = await runner.RunAsync("test:normal", appointment.Id);

        Assert.Equal(TraceOutcome.Committed, trace!.Outcome);
        Assert.Equal(2, trace.Steps.Count);
        Assert.Single(db.Context.DeparturePlans);
        Assert.Empty(db.Context.Nudges);
        Assert.Equal("Planned the dentist trip.", trace.FinalMessage);
    }

    [Fact]
    public async Task Nudge_window_run_sends_one_leave_nudge_naming_time_and_mode()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // window: 08:37Z − 10 min = 08:27Z
        FreshLocation();
        model
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueDynamic(m => new ChatResult(null, [CommitBest(m)], ModelProvider.MetaApi))
            .EnqueueDynamic(m =>
            {
                var leaveAt = LastToolResult(m).GetProperty("leaveAt").GetString();
                return new ChatResult(null,
                    [Call("notify.send", new { kind = "Leave", text = $"Leave at {leaveAt} by bike for the dentist." })],
                    ModelProvider.MetaApi);
            })
            .EnqueueFinal("Nudged.");

        var trace = await runner.RunAsync("test:window", appointment.Id);

        Assert.Equal(TraceOutcome.Committed, trace!.Outcome);
        var nudge = Assert.Single(db.Context.Nudges);
        Assert.Equal(NudgeKind.Leave, nudge.Kind);
        Assert.Equal(NudgeStatus.Sent, nudge.Status);
        Assert.Contains("09:37", nudge.Text);
        Assert.Contains("bike", nudge.Text);
        Assert.Equal(trace.Id, nudge.AgentTraceId);
    }

    [Fact]
    public async Task Second_identical_nudge_is_refused_as_duplicate()
    {
        await Nudge_window_run_sends_one_leave_nudge_naming_time_and_mode();

        model
            .EnqueueToolCalls(Call("notify.send", new { kind = "Leave", text = "Leave at 09:37 by bike for the dentist." }))
            .EnqueueFinal("Already sent.");

        var trace = await runner.RunAsync("test:window-again", appointment.Id);

        Assert.Single(db.Context.Nudges);
        Assert.Contains("duplicate", trace!.Steps.Last().ResultSummary);
    }

    [Fact]
    public async Task Fabricated_optionId_is_refused_and_nothing_is_committed()
    {
        model
            .EnqueueToolCalls(Call("plan.commit", new { optionId = "deadbeef", reason = "made up" }))
            .EnqueueFinal("Could not commit.");

        var trace = await runner.RunAsync("test:fabricated", appointment.Id);

        Assert.Equal(TraceOutcome.NoAction, trace!.Outcome);
        Assert.Empty(db.Context.DeparturePlans);
        Assert.Contains("commit refused", trace.Steps[0].ResultSummary);
    }

    [Fact]
    public async Task An_optionId_from_the_previous_options_call_in_the_same_run_is_refused()
    {
        model
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueDynamic(m =>
            {
                var staleId = FirstToolResult(m).GetProperty("options")[0].GetProperty("optionId").GetString();
                return new ChatResult(null, [Call("plan.commit", new { optionId = staleId, reason = "stale" })], ModelProvider.MetaApi);
            })
            .EnqueueFinal("Done.");

        var trace = await runner.RunAsync("test:stale-option", appointment.Id);

        Assert.Equal(TraceOutcome.NoAction, trace!.Outcome);
        Assert.Empty(db.Context.DeparturePlans);
        Assert.Contains("commit refused", trace.Steps.Last().ResultSummary);
    }

    [Fact]
    public async Task The_loop_ends_after_the_8th_tool_call_and_marks_the_trace_truncated()
    {
        for (var turn = 0; turn < 4; turn++)
            model.EnqueueToolCalls(
                Call("trace.note", new { text = $"note {turn}a" }),
                Call("trace.note", new { text = $"note {turn}b" }));

        var trace = await runner.RunAsync("test:limit", appointment.Id);

        Assert.Equal(TraceOutcome.Truncated, trace!.Outcome);
        Assert.Equal(8, trace.Steps.Count);
    }

    [Fact]
    public async Task Unknown_tool_returns_an_error_result_and_takes_no_action()
    {
        model
            .EnqueueToolCalls(Call("calendar.delete", new { id = "x" }))
            .EnqueueFinal("That tool does not exist.");

        var trace = await runner.RunAsync("test:unknown-tool", appointment.Id);

        Assert.Contains("unknown tool", trace!.Steps[0].ResultSummary);
        Assert.Equal(TraceOutcome.NoAction, trace.Outcome);
    }

    [Fact]
    public async Task Late_state_commits_the_skip_stop_recovery_and_sends_one_late_nudge()
    {
        // Confirmed coffee habit; with-stop leaves 09:25 local, without 09:37. At 09:30 local
        // only skipping the coffee is feasible.
        db.Context.Add(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = cafe.Id,
            Place = cafe,
            State = HabitState.Confirmed,
            AvgDurationMin = 8,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null),
        });
        db.Context.Add(new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Version = 1,
            Mode = TravelMode.Bicycle,
            LeaveAt = DateTimeOffset.Parse("2026-10-27T08:25:00Z"),
            ArriveAt = DateTimeOffset.Parse("2026-10-27T08:55:00Z"),
            Stops = [new PlanStop(cafe.Id, "Bar Bello", 8)],
            Reason = "earlier plan",
        });
        db.Context.SaveChanges();
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // past 08:25 → late
        FreshLocation();

        model
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueDynamic(m => new ChatResult(null, [CommitBest(m)], ModelProvider.MetaApi))
            .EnqueueDynamic(m =>
            {
                var commit = LastToolResult(m);
                var text = $"Running late — skip Bar Bello and leave at {commit.GetProperty("leaveAt").GetString()} by bike to arrive {commit.GetProperty("arriveAt").GetString()}.";
                return new ChatResult(null, [Call("notify.send", new { kind = "Late", text })], ModelProvider.MetaApi);
            })
            .EnqueueFinal("Recovery sent.");

        var trace = await runner.RunAsync("tick:late", appointment.Id);

        Assert.Equal(TraceOutcome.Committed, trace!.Outcome);
        var plan = db.Context.DeparturePlans.OrderByDescending(p => p.Version).First();
        Assert.Equal(2, plan.Version);
        Assert.Empty(plan.Stops); // the skip-stop option
        var nudge = Assert.Single(db.Context.Nudges);
        Assert.Equal(NudgeKind.Late, nudge.Kind);
        Assert.Contains("skip Bar Bello", nudge.Text);
    }

    [Fact]
    public async Task Nothing_feasible_commits_the_least_late_option_and_names_the_arrival()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:55:00Z")); // leaveAt 08:37Z long past
        FreshLocation();

        model
            .EnqueueToolCalls(Call("plan.options", new { appointmentId = appointment.Id }))
            .EnqueueDynamic(m => new ChatResult(null, [CommitBest(m, preferFeasible: false)], ModelProvider.MetaApi))
            .EnqueueDynamic(m =>
            {
                var commit = LastToolResult(m);
                var text = $"You can't make it on time — leave now at {commit.GetProperty("leaveAt").GetString()} by bike and you'll arrive {commit.GetProperty("arriveAt").GetString()}.";
                return new ChatResult(null, [Call("notify.send", new { kind = "Late", text })], ModelProvider.MetaApi);
            })
            .EnqueueFinal("Told them the earliest arrival.");

        var trace = await runner.RunAsync("tick:late", appointment.Id);

        Assert.Equal(TraceOutcome.Committed, trace!.Outcome);
        var plan = Assert.Single(db.Context.DeparturePlans);
        Assert.False(plan.IsFeasible);
        var nudge = Assert.Single(db.Context.Nudges);
        Assert.Contains("10:13", nudge.Text); // 09:55 local + 18 min bike
    }

    public void Dispose()
    {
        provider.Dispose();
        db.Dispose();
    }
}
