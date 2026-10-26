using System.Reflection;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Domain;

public sealed class ConfirmedHabitTests
{
    private static readonly ContextKey Context =
        new(DayOfWeek.Thursday, TimeBucket.Evening, TravelDirection.TowardHome, null);

    [Fact]
    public void ConfirmedHabit_has_no_public_constructor()
    {
        Assert.Empty(typeof(ConfirmedHabit).GetConstructors(BindingFlags.Public | BindingFlags.Instance));
    }

    [Fact]
    public void FromPersisted_refuses_a_habit_that_is_not_confirmed()
    {
        var habit = new Habit { Id = Guid.NewGuid(), PlaceId = Guid.NewGuid(), Context = Context, State = HabitState.Proposed };
        Assert.Throws<InvalidOperationException>(() => ConfirmHabitHandler.FromPersisted(habit));
    }

    [Fact]
    public async Task ConfirmAsync_confirms_publishes_and_returns_a_ConfirmedHabit()
    {
        using var db = new TestDb();
        var place = new Place { Id = Guid.NewGuid(), Name = "Albert Heijn", Lat = 52.09, Lng = 5.12 };
        var habit = new Habit { Id = Guid.NewGuid(), PlaceId = place.Id, Place = place, Context = Context, AvgDurationMin = 15 };
        db.Context.AddRange(place, habit);
        await db.Context.SaveChangesAsync();
        var events = new CollectingDomainEvents();

        var confirmed = await new ConfirmHabitHandler(db.Context, events).ConfirmAsync(habit.Id);

        Assert.Equal(HabitState.Confirmed, habit.State);
        Assert.NotNull(habit.DecidedAt);
        Assert.Equal("Albert Heijn", confirmed.PlaceName);
        Assert.Equal(15, confirmed.AvgDurationMin);
        var evt = Assert.Single(events.Published.OfType<HabitConfirmed>());
        Assert.Equal(habit.Id, evt.HabitId);
    }

    [Fact]
    public async Task A_rejected_habit_cannot_be_confirmed()
    {
        using var db = new TestDb();
        var place = new Place { Id = Guid.NewGuid(), Name = "x", Lat = 0, Lng = 0 };
        var habit = new Habit { Id = Guid.NewGuid(), PlaceId = place.Id, Place = place, Context = Context, State = HabitState.Rejected };
        db.Context.AddRange(place, habit);
        await db.Context.SaveChangesAsync();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => new ConfirmHabitHandler(db.Context, new CollectingDomainEvents()).ConfirmAsync(habit.Id));
    }
}
