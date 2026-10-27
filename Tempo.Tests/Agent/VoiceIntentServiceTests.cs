using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Agent;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Location;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Agent;

/// <summary>Voice-input spec: each intent from a fixed transcript via the scripted fake;
/// Unknown returns the three suggestions; undo reverts the input change and re-runs.</summary>
public sealed class VoiceIntentServiceTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T08:00:00Z"));
    private readonly ScriptedMetaModels model = new();
    private readonly CollectingDomainEvents events = new();
    private readonly Appointment appointment;
    private readonly Place barBello;
    private readonly Place albertHeijn;

    public VoiceIntentServiceTests()
    {
        var destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.11 };
        barBello = new Place { Id = Guid.NewGuid(), Name = "Bar Bello", Lat = 52.09, Lng = 5.2 };
        albertHeijn = new Place { Id = Guid.NewGuid(), Name = "Albert Heijn", Lat = 52.091, Lng = 5.21 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Team meeting",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            End = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
            PlaceId = destination.Id,
            Place = destination,
        };
        var plan = new DeparturePlan
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            Version = 1,
            Mode = TravelMode.Car,
            LeaveAt = DateTimeOffset.Parse("2026-10-27T08:37:00Z"),
            ArriveAt = DateTimeOffset.Parse("2026-10-27T08:55:00Z"),
            Stops = [new PlanStop(barBello.Id, "Bar Bello", 9)],
            Reason = "test",
        };
        db.Context.AddRange(destination, barBello, albertHeijn, appointment, plan,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private VoiceIntentService Service()
    {
        var plans = new PlanService(db.Context,
            new DeparturePlanner(new FakeRoutingClient(), NullLogger<DeparturePlanner>.Instance),
            new PlanOptionStore(),
            new CurrentLocationProvider(new FakeLocationBuffer(), clock),
            clock, NullLogger<PlanService>.Instance);
        return new VoiceIntentService(db.Context, model, plans,
            new ConfirmHabitHandler(db.Context, events), events, clock,
            NullLogger<VoiceIntentService>.Instance);
    }

    private PlanInputs Inputs() => Assert.Single(db.Context.PlanInputs.Where(p => p.AppointmentId == appointment.Id));

    [Fact]
    public async Task Skip_the_coffee_writes_the_stop_into_skipped_place_ids()
    {
        model.EnqueueTyped(new IntentClassification { Type = "SkipStop", StopName = "Bar Bello" });

        var result = await Service().HandleTextAsync("skip the coffee today");

        Assert.True(result.Applied);
        Assert.Contains("Bar Bello", result.Change);
        Assert.Contains(barBello.Id, Inputs().SkippedPlaceIds);
        var applied = Assert.Single(events.Published.OfType<IntentApplied>());
        Assert.Equal("SkipStop", applied.Kind);
        Assert.Equal(appointment.Id, applied.AppointmentId);
    }

    [Fact]
    public async Task Skip_without_a_name_falls_back_to_the_single_stop()
    {
        model.EnqueueTyped(new IntentClassification { Type = "SkipStop" });

        var result = await Service().HandleTextAsync("no stop today");

        Assert.True(result.Applied);
        Assert.Contains(barBello.Id, Inputs().SkippedPlaceIds);
    }

    [Fact]
    public async Task Dutch_add_stop_resolves_the_place_by_lookup_not_model_output()
    {
        model.EnqueueTyped(new IntentClassification { Type = "AddStop", PlaceName = "Albert Heijn" });

        var result = await Service().HandleTextAsync("ik ga eerst even naar de Albert Heijn");

        Assert.True(result.Applied);
        var extra = Assert.Single(Inputs().ExtraStops);
        Assert.Equal(albertHeijn.Id, extra.PlaceId); // id from the Places table, not the model
        Assert.Equal(10, extra.DurationMin); // default when no duration spoken
    }

    [Fact]
    public async Task Adding_an_unknown_place_is_not_applied()
    {
        model.EnqueueTyped(new IntentClassification { Type = "AddStop", PlaceName = "Blokker" });

        var result = await Service().HandleTextAsync("even langs de Blokker");

        Assert.False(result.Applied);
        Assert.Contains("Blokker", result.Change);
        Assert.Empty(db.Context.PlanInputs);
    }

    [Fact]
    public async Task Running_late_sets_the_offset_clamped_to_sane_minutes()
    {
        model.EnqueueTyped(new IntentClassification { Type = "RunningLate", Minutes = 10 });

        var result = await Service().HandleTextAsync("I'm running ten minutes late");

        Assert.True(result.Applied);
        Assert.Equal(10, Inputs().LateOffsetMin);
    }

    [Fact]
    public async Task Leaving_now_triggers_a_run_without_touching_plan_inputs()
    {
        model.EnqueueTyped(new IntentClassification { Type = "LeavingNow" });

        var result = await Service().HandleTextAsync("leaving now");

        Assert.True(result.Applied);
        Assert.Empty(db.Context.PlanInputs);
        Assert.Equal("LeavingNow", Assert.Single(events.Published.OfType<IntentApplied>()).Kind);
    }

    [Fact]
    public async Task Pin_mode_parses_synonyms_in_code()
    {
        model.EnqueueTyped(new IntentClassification { Type = "PinMode", Mode = "fiets" });

        var result = await Service().HandleTextAsync("we gaan met de fiets");

        Assert.True(result.Applied);
        Assert.Equal(TravelMode.Bicycle, Inputs().PinnedMode);
    }

    [Fact]
    public async Task Confirm_habit_routes_through_the_handler()
    {
        var habit = new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = barBello.Id,
            Place = barBello,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null),
            AvgDurationMin = 9,
            VisitCount = 5,
            State = HabitState.Proposed,
        };
        db.Context.Habits.Add(habit);
        db.Context.SaveChanges();
        model.EnqueueTyped(new IntentClassification { Type = "ConfirmHabit", HabitName = "Bar Bello" });

        var result = await Service().HandleTextAsync("yes, plan the coffee stop");

        Assert.True(result.Applied);
        Assert.Equal(HabitState.Confirmed, habit.State);
        Assert.Single(events.Published.OfType<HabitConfirmed>()); // the handler's event wakes the agent
        Assert.Empty(events.Published.OfType<IntentApplied>()); // no double run
    }

    [Fact]
    public async Task Unknown_returns_the_three_suggested_intents()
    {
        model.EnqueueTyped(new IntentClassification { Type = "Unknown" });

        var result = await Service().HandleTextAsync("what a lovely day");

        Assert.False(result.Applied);
        Assert.Equal("Unknown", result.Kind);
        Assert.Equal(["SkipStop", "AddStop", "RunningLate"], result.Suggestions);
        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task Undo_reverts_the_skip_and_re_runs()
    {
        model.EnqueueTyped(new IntentClassification { Type = "SkipStop", StopName = "Bar Bello" });
        var applied = await Service().HandleTextAsync("skip the coffee today");

        var undone = await Service().UndoAsync(applied.IntentId!.Value);

        Assert.NotNull(undone);
        Assert.Empty(Inputs().SkippedPlaceIds);
        Assert.NotNull(db.Context.Intents.Single().UndoneAt);
        Assert.Contains(events.Published.OfType<IntentApplied>(), e => e.Kind == "undo");
    }

    [Fact]
    public async Task Undo_twice_returns_nothing()
    {
        model.EnqueueTyped(new IntentClassification { Type = "RunningLate", Minutes = 5 });
        var applied = await Service().HandleTextAsync("running late");

        Assert.NotNull(await Service().UndoAsync(applied.IntentId!.Value));
        Assert.Null(await Service().UndoAsync(applied.IntentId!.Value));
    }

    [Fact]
    public async Task Audio_goes_through_transcription_first()
    {
        model.Transcript = "skip the coffee today";
        model.EnqueueTyped(new IntentClassification { Type = "SkipStop", StopName = "Bar Bello" });

        var result = await Service().HandleAudioAsync(new byte[1000]);

        Assert.Equal("skip the coffee today", result.Transcript);
        Assert.True(result.Applied);
    }

    [Fact]
    public async Task Oversized_audio_is_rejected_without_a_model_call()
    {
        var result = await Service().HandleAudioAsync(new byte[VoiceIntentService.MaxAudioBytes + 1]);

        Assert.False(result.Applied);
        Assert.Contains("15 seconds", result.Change);
    }

    public void Dispose() => db.Dispose();
}
