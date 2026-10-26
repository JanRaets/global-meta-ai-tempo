using Microsoft.Extensions.Logging.Abstractions;
using Tempo.Core.Domain;
using Tempo.Core.Geo;
using Tempo.Core.Planning;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Planning;

public sealed class StopSubsetTests
{
    [Fact]
    public async Task Two_habits_produce_all_each_skipped_and_none_subsets()
    {
        var context = new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.Other, null);
        ConfirmedHabit Confirmed(string name) => ConfirmHabitHandler.FromPersisted(new Habit
        {
            Id = Guid.NewGuid(),
            PlaceId = Guid.NewGuid(),
            Place = new Place { Id = Guid.NewGuid(), Name = name, Lat = 52.09, Lng = 5.2 },
            State = HabitState.Confirmed,
            AvgDurationMin = 8,
            Context = context,
        });
        var planner = new DeparturePlanner(new FakeRoutingClient(), NullLogger<DeparturePlanner>.Instance);
        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Title = "x",
            Start = DateTimeOffset.Parse("2026-10-27T09:00:00Z"),
            End = DateTimeOffset.Parse("2026-10-27T10:00:00Z"),
        };

        var result = await planner.Plan(appointment, new GeoPoint(52.0906, 5.2332), new GeoPoint(52.0894, 5.11),
            [Confirmed("A"), Confirmed("B")], [], [TravelMode.Bicycle],
            DateTimeOffset.Parse("2026-10-27T08:00:00Z"));

        // {A,B}, {A}, {B}, {} — not the powerset beyond that.
        Assert.Equal(4, result.Options.Count);
        Assert.Equal([0, 1, 1, 2], result.Options.Select(o => o.Stops.Count).Order().ToArray());
    }
}
