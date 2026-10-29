using Tempo.Core.Domain;

namespace Tempo.Tests.Domain;

public sealed class ContextKeyTests
{
    private static readonly ContextKey WeekdayMorningCoffee =
        new(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.AwayFromHome, "meeting");

    [Fact]
    public void Exact_context_matches()
    {
        Assert.True(WeekdayMorningCoffee.Matches(
            new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.AwayFromHome, "meeting")));
    }

    [Fact]
    public void Weekend_does_not_match_a_weekday_habit()
    {
        Assert.False(WeekdayMorningCoffee.Matches(
            new ContextKey(DayOfWeek.Saturday, TimeBucket.Morning, TravelDirection.AwayFromHome, "meeting")));
    }

    [Theory]
    [InlineData(TimeBucket.Evening)]
    [InlineData(TimeBucket.Midday)]
    public void Different_time_bucket_does_not_match(TimeBucket bucket)
    {
        Assert.False(WeekdayMorningCoffee.Matches(
            new ContextKey(DayOfWeek.Tuesday, bucket, TravelDirection.AwayFromHome, "meeting")));
    }

    [Fact]
    public void Different_direction_does_not_match()
    {
        Assert.False(WeekdayMorningCoffee.Matches(
            new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.TowardHome, "meeting")));
    }

    [Fact]
    public void Habit_with_adjacent_kind_requires_it_to_match()
    {
        Assert.False(WeekdayMorningCoffee.Matches(
            new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.AwayFromHome, null)));
    }

    // A null weekday means "any Monday–Friday" (ContextKey amendment, 29 Oct 2026) — the
    // seeded demo coffee habit matches a morning meeting on every weekday, never the weekend.
    [Theory]
    [InlineData(DayOfWeek.Monday)]
    [InlineData(DayOfWeek.Wednesday)]
    [InlineData(DayOfWeek.Friday)]
    public void Weekday_generalised_habit_matches_every_weekday(DayOfWeek day)
    {
        var anyWeekday = WeekdayMorningCoffee with { Weekday = null };
        Assert.True(anyWeekday.Matches(
            new ContextKey(day, TimeBucket.Morning, TravelDirection.AwayFromHome, "meeting")));
    }

    [Theory]
    [InlineData(DayOfWeek.Saturday)]
    [InlineData(DayOfWeek.Sunday)]
    public void Weekday_generalised_habit_does_not_match_the_weekend(DayOfWeek day)
    {
        var anyWeekday = WeekdayMorningCoffee with { Weekday = null };
        Assert.False(anyWeekday.Matches(
            new ContextKey(day, TimeBucket.Morning, TravelDirection.AwayFromHome, "meeting")));
    }

    [Fact]
    public void Habit_without_adjacent_kind_matches_any_appointment_kind()
    {
        var anyKind = WeekdayMorningCoffee with { AdjacentAppointmentKind = null };
        Assert.True(anyKind.Matches(
            new ContextKey(DayOfWeek.Tuesday, TimeBucket.Morning, TravelDirection.AwayFromHome, "dentist")));
    }

    [Theory]
    [InlineData(5, 0, TimeBucket.Early)]
    [InlineData(8, 59, TimeBucket.Early)]
    [InlineData(9, 0, TimeBucket.Morning)]
    [InlineData(12, 0, TimeBucket.Midday)]
    [InlineData(14, 0, TimeBucket.Afternoon)]
    [InlineData(18, 0, TimeBucket.Evening)]
    [InlineData(23, 30, TimeBucket.Evening)]
    [InlineData(2, 0, TimeBucket.Evening)]
    public void Time_buckets_have_documented_boundaries(int hour, int minute, TimeBucket expected)
    {
        Assert.Equal(expected, ContextKey.BucketOf(new TimeOnly(hour, minute)));
    }
}
