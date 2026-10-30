using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Core.Domain;
using Tempo.Jobs;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Habits;

/// <summary>Amsterdam local times; 2026-10-22 was a Thursday. The scripted model words
/// proposals; EnqueueTyped failures fall back to the deterministic template.</summary>
public sealed class HabitInferenceTests : IDisposable
{
    private static readonly TimeZoneInfo Tz = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-26T20:00:00Z"));
    private readonly ScriptedMetaModels model = new();
    private readonly Place supermarket;
    private readonly Place home;

    public HabitInferenceTests()
    {
        home = new Place { Id = Guid.NewGuid(), Name = "Thuis", Lat = 52.095, Lng = 5.24 };
        supermarket = new Place { Id = Guid.NewGuid(), Name = "Albert Heijn", Lat = 52.092, Lng = 5.21 };
        db.Context.AddRange(home, supermarket,
            new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k", HomePlaceId = home.Id });
        db.Context.SaveChanges();
    }

    private HabitInferenceService Service() => new(db.Context,
        new HabitWording(model, NullLogger<HabitWording>.Instance), clock, NullLogger<HabitInferenceService>.Instance);

    private static DateTimeOffset ThursdayEvening(int weeksBack, int minute = 10) =>
        new(new DateTime(2026, 10, 22, 18, minute, 0).AddDays(-7 * weeksBack), TimeSpan.FromHours(2)); // CEST/CET edge-safe: week 0 = 22 Oct

    private void AddSupermarketVisit(int weeksBack, int durationMin, bool homeAfter = true)
    {
        var arrive = ThursdayEvening(weeksBack);
        db.Context.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = supermarket.Id, ArrivedAt = arrive, DepartedAt = arrive.AddMinutes(durationMin) });
        if (homeAfter)
            db.Context.Visits.Add(new Visit { Id = Guid.NewGuid(), PlaceId = home.Id, ArrivedAt = arrive.AddMinutes(durationMin + 10), DepartedAt = arrive.AddMinutes(durationMin + 120) });
        db.Context.SaveChanges();
    }

    [Fact]
    public async Task Three_regular_thursday_evening_visits_toward_home_become_one_proposed_habit()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);
        AddSupermarketVisit(3, 15);
        model.EnqueueTyped(new HabitProposalText("You usually pop into Albert Heijn on Thursday evenings — plan for it?", "3 visits, ~15 min"));

        var created = await Service().RunAsync();

        Assert.Equal(1, created);
        var habit = Assert.Single(db.Context.Habits);
        Assert.Equal(HabitState.Proposed, habit.State);
        Assert.Equal(15, habit.AvgDurationMin);
        Assert.Equal(3, habit.VisitCount);
        Assert.Equal(DayOfWeek.Thursday, habit.Context.Weekday);
        Assert.Equal(TimeBucket.Evening, habit.Context.TimeBucket);
        Assert.Equal(TravelDirection.TowardHome, habit.Context.Direction);
        Assert.Contains("Albert Heijn", habit.ProposalText);
    }

    [Fact]
    public async Task Two_visits_are_insufficient_evidence()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);

        Assert.Equal(0, await Service().RunAsync());
        Assert.Empty(db.Context.Habits);
    }

    [Fact]
    public async Task Irregular_durations_do_not_become_a_habit()
    {
        AddSupermarketVisit(1, 5);
        AddSupermarketVisit(2, 40);
        AddSupermarketVisit(3, 90);

        Assert.Equal(0, await Service().RunAsync());
        Assert.Empty(db.Context.Habits);
    }

    [Fact]
    public async Task A_decided_habit_is_not_duplicated_and_a_fresh_rejection_is_not_reproposed()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);
        AddSupermarketVisit(3, 15);
        model.EnqueueTyped(new HabitProposalText("Plan Albert Heijn?", "3 visits, ~15 min"));
        await Service().RunAsync();
        var habit = Assert.Single(db.Context.Habits);
        habit.State = HabitState.Rejected;
        habit.DecidedAt = clock.GetUtcNow().AddDays(-10);
        db.Context.SaveChanges();

        Assert.Equal(0, await Service().RunAsync()); // rejected 10 days ago → suppressed
        Assert.Equal(HabitState.Rejected, Assert.Single(db.Context.Habits).State);
    }

    [Fact]
    public async Task A_rejection_older_than_30_days_is_reproposed()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);
        AddSupermarketVisit(3, 15);
        model.EnqueueTyped(new HabitProposalText("Plan Albert Heijn?", "3 visits, ~15 min"));
        await Service().RunAsync();
        var habit = Assert.Single(db.Context.Habits);
        habit.State = HabitState.Rejected;
        habit.DecidedAt = clock.GetUtcNow().AddDays(-40);
        db.Context.SaveChanges();
        model.EnqueueTyped(new HabitProposalText("Plan Albert Heijn again?", "3 visits, ~15 min"));

        Assert.Equal(1, await Service().RunAsync());
        habit = Assert.Single(db.Context.Habits); // same row, re-proposed
        Assert.Equal(HabitState.Proposed, habit.State);
        Assert.Null(habit.DecidedAt);
    }

    [Fact]
    public async Task Model_text_that_alters_the_numbers_is_replaced_by_the_template()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);
        AddSupermarketVisit(3, 15);
        model.EnqueueTyped(new HabitProposalText("You stop at Albert Heijn for 45 min!", "3 visits"));

        await Service().RunAsync();

        var habit = Assert.Single(db.Context.Habits);
        Assert.DoesNotContain("45", habit.ProposalText);
        Assert.Contains("15 min", habit.ProposalText); // template carries the computed average
        Assert.Equal(15, habit.AvgDurationMin);
    }

    [Fact]
    public async Task Model_failure_falls_back_to_the_template()
    {
        AddSupermarketVisit(1, 14);
        AddSupermarketVisit(2, 16);
        AddSupermarketVisit(3, 15);
        // nothing enqueued → the fake throws → template path

        Assert.Equal(1, await Service().RunAsync());
        Assert.Contains("Albert Heijn", Assert.Single(db.Context.Habits).ProposalText);
    }

    /// <summary>One comparable Friday (chained-origin amendment, 30 Oct 2026): hockey
    /// 16:00–18:00 local, dinner at 21:00, a home visit in the 3 h gap between them.</summary>
    private void AddHockeyThenDinnerDay(int weeksBack, Place hockeyPlace, Place dinnerPlace)
    {
        var hockeyStart = new DateTimeOffset(new DateTime(2026, 10, 23, 14, 0, 0).AddDays(-7 * weeksBack), TimeSpan.Zero);
        db.Context.AddRange(
            new Appointment
            {
                Id = Guid.NewGuid(), Title = "Hockey JO9", PlaceId = hockeyPlace.Id,
                Start = hockeyStart, End = hockeyStart.AddHours(2),
            },
            new Appointment
            {
                Id = Guid.NewGuid(), Title = "Dinner at Jos", PlaceId = dinnerPlace.Id,
                Start = hockeyStart.AddHours(5), End = hockeyStart.AddHours(7),
            },
            new Visit
            {
                Id = Guid.NewGuid(), PlaceId = home.Id,
                ArrivedAt = hockeyStart.AddHours(2.5), DepartedAt = hockeyStart.AddHours(4.5),
            });
        db.Context.SaveChanges();
    }

    [Fact]
    public async Task Three_fridays_home_between_hockey_and_dinner_propose_ReturnsHome()
    {
        var hockeyPlace = new Place { Id = Guid.NewGuid(), Name = "Schaerweijde", Lat = 52.1042, Lng = 5.2658 };
        var dinnerPlace = new Place { Id = Guid.NewGuid(), Name = "Bij Jos", Lat = 52.0894, Lng = 5.1100 };
        db.Context.AddRange(hockeyPlace, dinnerPlace);
        db.Context.SaveChanges();
        AddHockeyThenDinnerDay(1, hockeyPlace, dinnerPlace);
        AddHockeyThenDinnerDay(2, hockeyPlace, dinnerPlace);
        AddHockeyThenDinnerDay(3, hockeyPlace, dinnerPlace);
        // nothing enqueued → the template words the proposal

        Assert.Equal(1, await Service().RunAsync());

        var habit = Assert.Single(db.Context.Habits);
        Assert.Equal(HabitKind.ReturnsHome, habit.Kind);
        Assert.Equal(HabitState.Proposed, habit.State);
        Assert.Equal(home.Id, habit.PlaceId);
        Assert.Equal(3, habit.VisitCount);
        Assert.Equal(DayOfWeek.Friday, habit.Context.Weekday);
        Assert.Equal("personal", habit.Context.AdjacentAppointmentKind);
        Assert.Equal("long", habit.Context.GapBucket);
        Assert.Contains("plan from home", habit.ProposalText);
    }

    [Fact]
    public async Task Two_comparable_days_do_not_propose_ReturnsHome()
    {
        var hockeyPlace = new Place { Id = Guid.NewGuid(), Name = "Schaerweijde", Lat = 52.1042, Lng = 5.2658 };
        var dinnerPlace = new Place { Id = Guid.NewGuid(), Name = "Bij Jos", Lat = 52.0894, Lng = 5.1100 };
        db.Context.AddRange(hockeyPlace, dinnerPlace);
        db.Context.SaveChanges();
        AddHockeyThenDinnerDay(1, hockeyPlace, dinnerPlace);
        AddHockeyThenDinnerDay(2, hockeyPlace, dinnerPlace);

        Assert.Equal(0, await Service().RunAsync());
        Assert.Empty(db.Context.Habits);
    }

    [Fact]
    public void Direction_derives_from_surrounding_home_visits()
    {
        var arrive = ThursdayEvening(1);
        var visit = new Visit { Id = Guid.NewGuid(), PlaceId = supermarket.Id, ArrivedAt = arrive, DepartedAt = arrive.AddMinutes(15) };
        var homeBefore = new Visit { Id = Guid.NewGuid(), PlaceId = home.Id, ArrivedAt = arrive.AddHours(-2), DepartedAt = arrive.AddMinutes(-10) };
        var homeAfter = new Visit { Id = Guid.NewGuid(), PlaceId = home.Id, ArrivedAt = arrive.AddMinutes(30), DepartedAt = arrive.AddHours(3) };

        Assert.Equal(TravelDirection.TowardHome,
            HabitInferenceService.DeriveContext(visit, [homeBefore, visit, homeAfter], [], home.Id, Tz).Direction);
        Assert.Equal(TravelDirection.AwayFromHome,
            HabitInferenceService.DeriveContext(visit, [homeBefore, visit], [], home.Id, Tz).Direction);
        Assert.Equal(TravelDirection.Other,
            HabitInferenceService.DeriveContext(visit, [visit], [], home.Id, Tz).Direction);
        Assert.Equal(TravelDirection.Other,
            HabitInferenceService.DeriveContext(visit, [homeBefore, visit, homeAfter], [], null, Tz).Direction);
    }

    [Fact]
    public void Adjacent_appointment_kind_comes_from_the_nearest_appointment_within_two_hours()
    {
        var arrive = ThursdayEvening(1);
        var visit = new Visit { Id = Guid.NewGuid(), PlaceId = supermarket.Id, ArrivedAt = arrive, DepartedAt = arrive.AddMinutes(15) };
        var meeting = new Appointment { Id = Guid.NewGuid(), Title = "Team meeting", Start = arrive.AddMinutes(40), End = arrive.AddMinutes(100) };
        var farAway = new Appointment { Id = Guid.NewGuid(), Title = "Dentist", Start = arrive.AddHours(5), End = arrive.AddHours(6) };

        Assert.Equal("meeting", HabitInferenceService.DeriveContext(visit, [visit], [meeting, farAway], null, Tz).AdjacentAppointmentKind);
        Assert.Null(HabitInferenceService.DeriveContext(visit, [visit], [farAway], null, Tz).AdjacentAppointmentKind);
    }

    public void Dispose() => db.Dispose();
}
