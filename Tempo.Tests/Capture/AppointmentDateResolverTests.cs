using Tempo.Agent;

namespace Tempo.Tests.Capture;

/// <summary>Relative-date resolution (capture-appointment spec): the model returns text,
/// this resolver computes. Reference week: Tue 27 Oct 2026, Europe/Amsterdam (CET).</summary>
public sealed class AppointmentDateResolverTests
{
    private static readonly TimeZoneInfo Amsterdam = TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");

    private static DateTimeOffset Now(int day, int hour, int minute = 0) =>
        new(2026, 10, day, hour, minute, 0, TimeSpan.FromHours(1));

    private static DateTimeOffset At(int year, int month, int day, int hour, int minute = 0) =>
        new(year, month, day, hour, minute, 0, TimeSpan.FromHours(1));

    private static DateTimeOffset? Resolve(string? date, string? time, DateTimeOffset now) =>
        AppointmentDateResolver.Resolve(date, time, now, Amsterdam);

    [Fact]
    public void English_day_name_resolves_to_the_coming_weekday()
    {
        Assert.Equal(At(2026, 10, 29, 19), Resolve("Thursday", "19:00", Now(26, 10))); // Mon → Thu
    }

    [Fact]
    public void Dutch_day_name_matching_today_with_time_ahead_is_today()
    {
        Assert.Equal(At(2026, 10, 27, 19), Resolve("dinsdag", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Same_day_name_with_passed_time_moves_a_week()
    {
        Assert.Equal(At(2026, 11, 3, 19), Resolve("dinsdag", "19:00", Now(27, 20)));
    }

    [Fact]
    public void Dutch_two_letter_abbreviations()
    {
        var now = Now(27, 10); // a Tuesday
        Assert.Equal(At(2026, 10, 29, 19, 30), Resolve("do", "19:30", now));
        Assert.Equal(At(2026, 10, 30, 19), Resolve("vr", "19:00", now));
        Assert.Equal(At(2026, 11, 2, 9), Resolve("ma", "09:00", now)); // yesterday's name → next week
    }

    [Fact]
    public void Explicit_date_beats_weekday_when_they_disagree()
    {
        // 30 Oct 2026 is a Friday; a real letter saying "do 30 okt" has a typo in the
        // weekday — the explicit date wins.
        Assert.Equal(At(2026, 10, 30, 19, 30), Resolve("do 30 okt", "19:30", Now(27, 10)));
    }

    [Fact]
    public void Morgen_is_tomorrow()
    {
        Assert.Equal(At(2026, 10, 28, 8), Resolve("morgen", "08:00", Now(27, 23)));
    }

    [Fact]
    public void Vandaag_keeps_today_even_for_a_passed_time()
    {
        Assert.Equal(At(2026, 10, 27, 9), Resolve("vandaag", "09:00", Now(27, 10)));
    }

    [Fact]
    public void Numeric_date_is_day_first()
    {
        Assert.Equal(At(2026, 10, 30, 19), Resolve("30/10", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Ambiguous_numeric_pair_reads_day_first_European()
    {
        Assert.Equal(At(2026, 11, 10, 19), Resolve("10/11", "19:00", Now(27, 10))); // 10 Nov, not 11 Oct
    }

    [Fact]
    public void Full_date_with_year_is_exact()
    {
        Assert.Equal(At(2026, 10, 30, 19), Resolve("30-10-2026", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Passed_date_without_year_rolls_to_next_year()
    {
        // 20 Oct 2027 is still in daylight time (+02:00), unlike the +01:00 October dates above.
        Assert.Equal(new DateTimeOffset(2027, 10, 20, 19, 0, 0, TimeSpan.FromHours(2)),
            Resolve("20 okt", "19:00", Now(27, 10)));
    }

    [Fact]
    public void English_month_first_with_weekday()
    {
        Assert.Equal(At(2026, 10, 29, 19), Resolve("Thursday, October 29", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Dutch_u_times()
    {
        var now = Now(27, 10);
        Assert.Equal(At(2026, 10, 29, 19, 30), Resolve("donderdag", "19u30", now));
        Assert.Equal(At(2026, 10, 30, 19), Resolve("vrijdag", "19u", now));
    }

    [Fact]
    public void Time_only_defaults_to_today_when_ahead_tomorrow_when_passed()
    {
        Assert.Equal(At(2026, 10, 27, 19), Resolve("", "19:00", Now(27, 10)));
        Assert.Equal(At(2026, 10, 28, 19), Resolve("", "19:00", Now(27, 20)));
    }

    [Fact]
    public void Dotted_pair_reads_as_time_when_valid()
    {
        // "05.10" is a valid time, so it is 05:10 — already passed, hence tomorrow.
        Assert.Equal(At(2026, 10, 28, 5, 10), Resolve("05.10", "", Now(27, 10)));
    }

    [Fact]
    public void Dotted_pair_reads_as_day_month_when_not_a_valid_time()
    {
        Assert.Equal(At(2026, 10, 30, 19), Resolve("30.10", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Unparseable_date_with_time_returns_null()
    {
        Assert.Null(Resolve("someday", "19:00", Now(27, 10)));
    }

    [Fact]
    public void Missing_time_returns_null()
    {
        Assert.Null(Resolve("Thursday", "", Now(27, 10)));
        Assert.Null(Resolve("", "", Now(27, 10)));
    }

    [Fact]
    public void Case_and_whitespace_are_tolerated()
    {
        Assert.Equal(At(2026, 10, 29, 19), Resolve("  THURSDAY  ", " 19:00 ", Now(27, 10)));
    }

    [Fact]
    public void Leap_day_rolls_to_the_next_leap_year()
    {
        Assert.Equal(At(2028, 2, 29, 19), Resolve("29 feb", "19:00", Now(27, 10)));
    }
}
