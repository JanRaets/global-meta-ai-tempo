using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Agent;
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

public sealed class PlannerTickTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly ScriptedMetaModels model = new();
    private readonly RecordingNudgeTransport transport = new();
    private readonly ServiceProvider provider;
    private readonly PlannerTickService tick;
    private readonly PlanService plans;
    private readonly Appointment appointment;

    public PlannerTickTests()
    {
        var destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            End = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
            PlaceId = destination.Id,
            Place = destination,
            BufferMin = 5,
        };
        db.Context.AddRange(destination, appointment,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", EnabledModes = [TravelMode.Bicycle] });
        db.Context.SaveChanges();
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-2)));

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

        var runner = new AgentRunner(provider.GetRequiredService<IServiceScopeFactory>(), NullLogger<AgentRunner>.Instance);
        var location = provider.GetRequiredService<CurrentLocationProvider>();
        var optionStore = new PlanOptionStore();
        plans = new PlanService(db.Context,
            new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
            optionStore, location, clock, NullLogger<PlanService>.Instance);
        var nudges = new NudgeService(db.Context, optionStore, transport, clock, NullLogger<NudgeService>.Instance);
        var phases = new PlanPhaseService(db.Context, location, new Tempo.Core.Configuration.PhaseOptions(), clock,
            NullLogger<PlanPhaseService>.Instance);
        tick = new PlannerTickService(db.Context, runner, plans, phases, nudges, new TickStateStore(), location, clock,
            NullLogger<PlannerTickService>.Instance);
    }

    [Fact]
    public async Task Unchanged_inputs_make_no_model_call_on_the_second_tick()
    {
        model.EnqueueFinal("Nothing to do.");

        await tick.TickAsync();
        Assert.Single(model.ChatInvocations); // first tick: inputs are new → one run

        await tick.TickAsync();
        Assert.Single(model.ChatInvocations); // unchanged → no model call
    }

    [Fact]
    public async Task No_upcoming_appointment_makes_no_model_and_no_routing_call()
    {
        db.Context.Appointments.Remove(appointment);
        db.Context.SaveChanges();

        await tick.TickAsync();

        Assert.Empty(model.ChatInvocations);
        Assert.Empty(routing.Origins);
    }

    [Fact]
    public async Task An_opened_nudge_window_triggers_a_run_even_with_unchanged_inputs()
    {
        model.EnqueueFinal("Nothing to do.");
        await tick.TickAsync(); // hash stored

        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // leaveAt 08:37Z
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // window opens 08:27Z
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-1)));

        model.EnqueueFinal("Would nudge now.");
        await tick.TickAsync();

        // Location moved the hash too, so assert on the run count and the reason recorded.
        Assert.Equal(2, model.ChatInvocations.Count);
        var trace = db.Context.AgentTraces.AsEnumerable().OrderBy(t => t.StartedAt).Last();
        Assert.StartsWith("tick:", trace.Trigger);
    }

    [Fact]
    public async Task The_tick_does_not_rewake_the_model_once_the_leave_nudge_exists()
    {
        model.EnqueueFinal("Nothing to do.");
        await tick.TickAsync(); // stores the inputs hash
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // leaveAt 08:37Z
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        db.Context.Nudges.Add(new Nudge
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            PlanId = plan.Id,
            PlanVersion = plan.Version,
            Kind = NudgeKind.Leave,
            Text = "Leave at 09:37 by bike.",
            Status = NudgeStatus.Sent,
        });
        db.Context.SaveChanges();
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // window open, nudge already sent
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-1))); // same rounded coords

        await tick.TickAsync();

        Assert.Single(model.ChatInvocations); // every 2 minutes between nudge and leave: silence
    }

    [Fact]
    public async Task At_destination_commits_the_zero_travel_plan_without_the_model()
    {
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // real bike plan
        buffer.Points.Add(new LocationPoint(52.0894, 5.1100, clock.GetUtcNow().AddMinutes(-1))); // now at the dentist

        await tick.TickAsync();

        var plan = (await plans.CurrentAsync(appointment.Id))!;
        Assert.Equal(PlanPhase.AtDestination, plan.Phase);
        Assert.Equal(appointment.Start, plan.LeaveAt); // zero travel
        Assert.Equal(appointment.Start, plan.ArriveAt);
        Assert.Empty(plan.Stops);
        Assert.Equal("already at destination", plan.Reason);
        Assert.Empty(model.ChatInvocations); // deterministic — the model never woke
        Assert.Empty(transport.Delivered);   // and nothing was nudged
    }

    [Fact]
    public async Task En_route_movement_does_not_wake_the_model()
    {
        model.EnqueueFinal("Nothing to do.");
        await tick.TickAsync(); // stores the inputs hash
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // leaveAt 08:37Z
        var plan = (await plans.CurrentAsync(appointment.Id))!;
        plan.Phase = PlanPhase.EnRoute;
        plan.PhaseChangedAt = clock.GetUtcNow();
        (plan.LeaveFixLat, plan.LeaveFixLng) = (52.0906, 5.2332);
        db.Context.SaveChanges();

        // Driving: every fix changes the inputs hash, tick after tick.
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:45:00Z"));
        buffer.Points.Add(new LocationPoint(52.0900, 5.1800, clock.GetUtcNow().AddMinutes(-1)));
        await tick.TickAsync();

        Assert.Single(model.ChatInvocations); // deterministic refresh only — no run, no renudge
        Assert.Empty(transport.Delivered);
    }

    [Fact]
    public async Task A_failed_run_falls_back_to_a_deterministic_commit()
    {
        model.ThrowOnNextChat = new ModelUnavailableException("both providers down");

        await tick.TickAsync();

        var plan = Assert.Single(db.Context.DeparturePlans);
        Assert.Equal("fallback: model unavailable", plan.Reason);
        Assert.Equal(PlanCommitter.Fallback, plan.CommittedBy);
    }

    [Fact]
    public async Task A_run_that_ends_without_a_nudge_in_the_open_window_sends_the_template()
    {
        model.EnqueueFinal("Nothing to do.");
        await tick.TickAsync(); // stores the inputs hash
        var options = await plans.OptionsAsync(appointment.Id);
        await plans.CommitAsync(options.Options[0].OptionId, "test", PlanCommitter.Agent); // leaveAt 08:37Z
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:30:00Z")); // window open
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-1)));

        model.EnqueueFinal("Committed earlier; staying quiet."); // the model fumbles the nudge
        await tick.TickAsync();

        var nudge = Assert.Single(transport.Delivered);
        Assert.Equal(NudgeKind.Leave, nudge.Kind);
        Assert.Contains("09:37", nudge.Text); // local leave time, from the plan, not the model
        Assert.Contains("bicycle", nudge.Text);
    }

    [Fact]
    public async Task A_failed_run_in_the_late_state_still_nudges_via_the_template()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:45:00Z")); // past leaveAt 08:37Z
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-1)));
        model.ThrowOnNextChat = new ModelUnavailableException("both providers down");

        await tick.TickAsync();

        var plan = Assert.Single(db.Context.DeparturePlans);
        Assert.Equal(PlanCommitter.Fallback, plan.CommittedBy);
        var nudge = Assert.Single(transport.Delivered);
        Assert.Equal(NudgeKind.Late, nudge.Kind);
        Assert.Contains("Leave now by bicycle", nudge.Text);
    }

    public void Dispose()
    {
        provider.Dispose();
        db.Dispose();
    }
}
