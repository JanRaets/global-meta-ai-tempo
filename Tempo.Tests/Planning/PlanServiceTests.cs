using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Core.Location;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Planning;

/// <summary>
/// Times: Europe/Amsterdam is UTC+1 on 27 Oct 2026 (a Tuesday). The appointment starts at
/// 10:00 local = 09:00Z; "now" is 09:00 local = 08:00Z unless a test says otherwise.
/// The spec scenarios: bike 18 min + buffer 5 → leave 09:37; +8 min habit +4 min detour → 09:25.
/// </summary>
public sealed class PlanServiceTests : IDisposable
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-27T08:00:00Z");
    private static readonly DateTimeOffset AppointmentStart = DateTimeOffset.Parse("2026-10-27T09:00:00Z");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(Now);
    private readonly FakeLocationBuffer buffer = new();
    private readonly FakeRoutingClient routing = new();
    private readonly PlanOptionStore store = new();

    private readonly Place destination;
    private readonly Appointment appointment;
    private readonly UserSettings settings;

    public PlanServiceTests()
    {
        destination = new Place { Id = Guid.NewGuid(), Name = "Utrecht Centraal", Lat = 52.0894, Lng = 5.1100 };
        appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "Dentist",
            Source = AppointmentSource.Synced,
            Start = AppointmentStart,
            End = AppointmentStart.AddHours(1),
            PlaceId = destination.Id,
            Place = destination,
            BufferMin = 5,
        };
        settings = new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", EnabledModes = [TravelMode.Bicycle] };
        db.Context.AddRange(destination, appointment, settings);
        db.Context.SaveChanges();
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, Now.AddMinutes(-5)));
    }

    private PlanService Service() => new(
        db.Context,
        new DeparturePlanner(routing, NullLogger<DeparturePlanner>.Instance),
        store,
        new CurrentLocationProvider(buffer, clock),
        clock,
        NullLogger<PlanService>.Instance);

    private Place AddConfirmedCoffeeHabit(HabitState state = HabitState.Confirmed)
    {
        var cafe = new Place { Id = Guid.NewGuid(), Name = "Bar Bello", Lat = 52.0905, Lng = 5.2000 };
        db.Context.Add(cafe);
        db.Context.Add(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = cafe.Id,
            Place = cafe,
            State = state,
            AvgDurationMin = 8,
            VisitCount = 4,
            Context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null),
        });
        db.Context.SaveChanges();
        return cafe;
    }

    [Fact]
    public async Task Plan_without_habits_leaves_at_0937()
    {
        var result = await Service().OptionsAsync(appointment.Id);

        var option = Assert.Single(result.Options);
        Assert.Equal(TravelMode.Bicycle, option.Mode);
        Assert.Empty(option.Stops);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:37:00Z"), option.LeaveAt); // 09:37 local
        Assert.True(option.IsFeasible);
        Assert.Equal(37, option.SlackMin);
    }

    [Fact]
    public async Task Plan_with_confirmed_habit_leaves_at_0925_and_lists_the_stop()
    {
        AddConfirmedCoffeeHabit();

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.Equal(2, result.Options.Count); // with stop, without
        var withStop = Assert.Single(result.Options, o => o.Stops.Count == 1);
        Assert.Equal("Bar Bello", withStop.Stops[0].Name);
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:25:00Z"), withStop.LeaveAt); // 09:25 local
    }

    [Fact]
    public async Task Proposed_habit_is_ignored()
    {
        AddConfirmedCoffeeHabit(HabitState.Proposed);

        var result = await Service().OptionsAsync(appointment.Id);

        var option = Assert.Single(result.Options);
        Assert.Empty(option.Stops);
    }

    [Fact]
    public async Task Two_modes_and_one_habit_enumerate_four_distinct_options()
    {
        settings.EnabledModes = [TravelMode.Bicycle, TravelMode.Walking];
        db.Context.SaveChanges();
        AddConfirmedCoffeeHabit();

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.Equal(4, result.Options.Count);
        Assert.Equal(4, result.Options.Select(o => o.OptionId).Distinct().Count());
        Assert.Equal(2, result.Options.Count(o => o.Mode == TravelMode.Walking));
    }

    [Fact]
    public async Task Pinned_mode_returns_only_that_mode()
    {
        settings.EnabledModes = [TravelMode.Bicycle, TravelMode.Car];
        settings.PinnedMode = TravelMode.Car;
        db.Context.SaveChanges();

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.All(result.Options, o => Assert.Equal(TravelMode.Car, o.Mode));
    }

    [Fact]
    public async Task When_nothing_is_feasible_slack_is_negative()
    {
        clock.SetUtcNow(DateTimeOffset.Parse("2026-10-27T08:50:00Z"));
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, clock.GetUtcNow().AddMinutes(-1)));

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.All(result.Options, o =>
        {
            Assert.False(o.IsFeasible);
            Assert.True(o.SlackMin < 0);
        });
    }

    [Fact]
    public async Task Valid_commit_creates_version_1_with_reason_and_stops()
    {
        AddConfirmedCoffeeHabit();
        var service = Service();
        var options = await service.OptionsAsync(appointment.Id);
        var pick = options.Options.Single(o => o.Stops.Count == 1);

        var result = await service.CommitAsync(pick.OptionId, "coffee fits comfortably", PlanCommitter.Agent);

        Assert.Equal(CommitStatus.Committed, result.Status);
        var plan = await service.CurrentAsync(appointment.Id);
        Assert.Equal(1, plan!.Version);
        Assert.Equal("coffee fits comfortably", plan.Reason);
        Assert.Equal(pick.LeaveAt, plan.LeaveAt);
        Assert.Single(plan.Stops);
    }

    [Fact]
    public async Task Fabricated_optionId_is_rejected_and_nothing_is_committed()
    {
        var service = Service();
        await service.OptionsAsync(appointment.Id);

        var result = await service.CommitAsync("deadbeef", "made up", PlanCommitter.Agent);

        Assert.Equal(CommitStatus.Rejected, result.Status);
        Assert.Null(await service.CurrentAsync(appointment.Id));
    }

    [Fact]
    public async Task OptionId_from_a_previous_options_call_is_stale_and_rejected()
    {
        var service = Service();
        var first = await service.OptionsAsync(appointment.Id);
        await service.OptionsAsync(appointment.Id); // newest call replaces the set

        var result = await service.CommitAsync(first.Options[0].OptionId, "stale", PlanCommitter.Agent);

        Assert.Equal(CommitStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task Routing_jitter_within_3_minutes_keeps_the_version()
    {
        var service = Service();
        var options = await service.OptionsAsync(appointment.Id);
        await service.CommitAsync(options.Options[0].OptionId, "initial", PlanCommitter.Agent);

        routing.BaseMinutes[TravelMode.Bicycle] = 20; // leaveAt shifts by 2 min
        options = await service.OptionsAsync(appointment.Id);
        var jitter = await service.CommitAsync(options.Options[0].OptionId, "re-plan", PlanCommitter.Agent);
        Assert.Equal(CommitStatus.Unchanged, jitter.Status);
        Assert.Equal(1, (await service.CurrentAsync(appointment.Id))!.Version);

        routing.BaseMinutes[TravelMode.Bicycle] = 28; // 10 min shift → real change
        options = await service.OptionsAsync(appointment.Id);
        var moved = await service.CommitAsync(options.Options[0].OptionId, "slower route", PlanCommitter.Agent);
        Assert.Equal(CommitStatus.Committed, moved.Status);
        Assert.Equal(2, moved.Plan!.Version);
    }

    [Fact]
    public async Task Fallback_commits_the_feasible_option_with_the_latest_leaveAt()
    {
        settings.EnabledModes = [TravelMode.Bicycle, TravelMode.Walking]; // walking leaves 07:55Z → infeasible
        db.Context.SaveChanges();

        var plan = await Service().FallbackCommitAsync(appointment.Id);

        Assert.NotNull(plan);
        Assert.Equal(TravelMode.Bicycle, plan.Mode);
        Assert.Equal("fallback: model unavailable", plan.Reason);
        Assert.Equal(PlanCommitter.Fallback, plan.CommittedBy);
    }

    [Fact]
    public async Task Fallback_still_commits_when_one_mode_is_rate_limited()
    {
        settings.EnabledModes = [TravelMode.Bicycle, TravelMode.Walking];
        db.Context.SaveChanges();
        routing.Unavailable.Add(TravelMode.Walking);

        var plan = await Service().FallbackCommitAsync(appointment.Id);

        Assert.NotNull(plan);
        Assert.Equal(TravelMode.Bicycle, plan.Mode);
    }

    [Fact]
    public async Task Stale_location_plans_from_the_home_place()
    {
        var home = new Place { Id = Guid.NewGuid(), Name = "Home", Lat = 52.1000, Lng = 5.2500 };
        db.Context.Add(home);
        settings.HomePlaceId = home.Id;
        db.Context.SaveChanges();
        buffer.Points.Clear();
        buffer.Points.Add(new LocationPoint(52.0906, 5.2332, Now.AddMinutes(-60))); // stale

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.NotEmpty(result.Options);
        Assert.Equal(52.1000, routing.Origins[0].Lat);
        Assert.Equal(5.2500, routing.Origins[0].Lng);
    }

    [Fact]
    public async Task No_location_and_no_home_place_yields_a_graceful_empty_result()
    {
        buffer.Points.Clear();

        var result = await Service().OptionsAsync(appointment.Id);

        Assert.Empty(result.Options);
        Assert.Contains("no current location", result.Note);
    }

    [Fact]
    public async Task Input_changes_skip_the_habit_add_the_extra_stop_and_apply_the_late_offset()
    {
        var cafe = AddConfirmedCoffeeHabit();
        var bakery = new Place { Id = Guid.NewGuid(), Name = "Bakkerij Vink", Lat = 52.0900, Lng = 5.1900 };
        db.Context.Add(bakery);
        db.Context.Add(new PlanInputs
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            SkippedPlaceIds = [cafe.Id],
            ExtraStops = [new ExtraStop(bakery.Id, "Bakkerij Vink", 5)],
            LateOffsetMin = 10,
        });
        db.Context.SaveChanges();

        var result = await Service().OptionsAsync(appointment.Id);

        var option = Assert.Single(result.Options); // habit skipped → one subset, extra stop in it
        Assert.DoesNotContain(option.Stops, s => s.PlaceId == cafe.Id);
        Assert.Contains(option.Stops, s => s.PlaceId == bakery.Id);
        // total = 18 base + 4 detour + 5 dwell = 27 → leave 08:28Z; effectiveNow 08:10Z → slack 18.
        Assert.Equal(DateTimeOffset.Parse("2026-10-27T08:28:00Z"), option.LeaveAt);
        Assert.Equal(18, option.SlackMin);
    }

    public void Dispose() => db.Dispose();
}
