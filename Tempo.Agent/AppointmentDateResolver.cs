using System.Text.RegularExpressions;

namespace Tempo.Agent;

/// <summary>
/// Turns the date/time WORDS the vision model read into a concrete start time
/// (capture-appointment spec: relative-date resolution in the user timezone).
/// Pure and deterministic: the model returns text, only this code computes (D1).
///
/// Conventions (each covered by a test):
/// - English and Dutch day names, full and abbreviated (ma/di/wo/do/vr/za/zo).
///   "Thursday" on a Monday is the coming Thursday; a day name matching today is
///   today when the time is still ahead, else a week later.
/// - "today"/"vandaag", "tomorrow"/"morgen" (whole words).
/// - Explicit dates: "30 okt", "October 29", "30/10", "30-10-2026". Numeric pairs
///   are day-first (European); without a year a passed date rolls to next year.
/// - 24h times: "19:00", "19.00", "19u", "19u30". A dotted pair that is a valid
///   time ("10.30") is read as a time; one that is not ("30.10") as day.month.
/// - Time-only input defaults to today when the time is ahead, else tomorrow.
/// - A non-blank date text that yields no date, or a missing time, resolves to
///   null: the confirm card lets the user correct it instead of guessing.
/// </summary>
public static class AppointmentDateResolver
{
    private static readonly Regex TimeColon = new(@"(\d{1,2})[:.](\d{2})(?::\d{2})?", RegexOptions.Compiled);

    private static readonly Regex TimeU = new(@"(\d{1,2})\s*u(?:ur\b)?\s*(\d{1,2})?",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex NumericDate = new(@"(\d{1,2})[/\-.](\d{1,2})(?:[/\-.](\d{4}))?",
        RegexOptions.Compiled);

    private static readonly Regex DayMonth = new(@"(\d{1,2})(?:ste|de|th|st|nd|rd|e)?\s*([a-z]+)\.?\s*(\d{4})?",
        RegexOptions.Compiled);

    private static readonly Regex MonthDay = new(@"([a-z]+)\.?\s*(\d{1,2})(?:ste|de|th|st|nd|rd|e)?,?\s*(\d{4})?",
        RegexOptions.Compiled);

    private static readonly Dictionary<string, DayOfWeek> Days = new(StringComparer.Ordinal)
    {
        ["monday"] = DayOfWeek.Monday, ["mon"] = DayOfWeek.Monday,
        ["maandag"] = DayOfWeek.Monday, ["ma"] = DayOfWeek.Monday,
        ["tuesday"] = DayOfWeek.Tuesday, ["tue"] = DayOfWeek.Tuesday,
        ["dinsdag"] = DayOfWeek.Tuesday, ["di"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["wed"] = DayOfWeek.Wednesday,
        ["woensdag"] = DayOfWeek.Wednesday, ["wo"] = DayOfWeek.Wednesday,
        ["thursday"] = DayOfWeek.Thursday, ["thu"] = DayOfWeek.Thursday,
        ["donderdag"] = DayOfWeek.Thursday, ["do"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["fri"] = DayOfWeek.Friday,
        ["vrijdag"] = DayOfWeek.Friday, ["vr"] = DayOfWeek.Friday,
        ["saturday"] = DayOfWeek.Saturday, ["sat"] = DayOfWeek.Saturday,
        ["zaterdag"] = DayOfWeek.Saturday, ["za"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday, ["sun"] = DayOfWeek.Sunday,
        ["zondag"] = DayOfWeek.Sunday, ["zo"] = DayOfWeek.Sunday,
    };

    private static readonly Dictionary<string, int> Months = new(StringComparer.Ordinal)
    {
        ["jan"] = 1, ["januari"] = 1, ["january"] = 1,
        ["feb"] = 2, ["februari"] = 2, ["february"] = 2,
        ["mrt"] = 3, ["mar"] = 3, ["maart"] = 3, ["march"] = 3,
        ["apr"] = 4, ["april"] = 4,
        ["mei"] = 5, ["may"] = 5,
        ["jun"] = 6, ["juni"] = 6, ["june"] = 6,
        ["jul"] = 7, ["juli"] = 7, ["july"] = 7,
        ["aug"] = 8, ["augustus"] = 8, ["august"] = 8,
        ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
        ["okt"] = 10, ["oct"] = 10, ["oktober"] = 10, ["october"] = 10,
        ["nov"] = 11, ["november"] = 11,
        ["dec"] = 12, ["december"] = 12,
    };

    private static readonly HashSet<string> TodayWords = new(StringComparer.Ordinal) { "today", "vandaag" };
    private static readonly HashSet<string> TomorrowWords = new(StringComparer.Ordinal) { "tomorrow", "morgen" };

    public static DateTimeOffset? Resolve(string? dateText, string? timeText, DateTimeOffset now, TimeZoneInfo tz)
    {
        var date = dateText ?? "";
        var time = timeText ?? "";
        if (!TryFindTime(time, out var timeOfDay) && !TryFindTime(date, out timeOfDay))
            return null; // a start needs a time; the card lets the user enter it

        var nowLocal = TimeZoneInfo.ConvertTime(now, tz);
        var day = FindDay(StripTimes(date), StripTimes(time), nowLocal.Date, timeOfDay, nowLocal.TimeOfDay);
        if (day is null)
        {
            if (!IsBlank(StripTimes(date)))
                return null; // a dateless but non-blank date text means "can't tell", not "any day"
            day = timeOfDay > nowLocal.TimeOfDay ? nowLocal.Date : nowLocal.Date.AddDays(1);
        }
        var local = day.Value.Add(timeOfDay);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }

    private static bool TryFindTime(string text, out TimeSpan timeOfDay)
    {
        var best = (-1, default(TimeSpan));
        foreach (Match m in TimeColon.Matches(text))
        {
            var h = int.Parse(m.Groups[1].Value);
            var min = int.Parse(m.Groups[2].Value);
            if (h <= 23 && min <= 59 && (best.Item1 is -1 || m.Index < best.Item1))
                best = (m.Index, new TimeSpan(h, min, 0));
        }
        foreach (Match m in TimeU.Matches(text))
        {
            var h = int.Parse(m.Groups[1].Value);
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            if (h <= 23 && min <= 59 && (best.Item1 is -1 || m.Index < best.Item1))
                best = (m.Index, new TimeSpan(h, min, 0));
        }
        timeOfDay = best.Item2;
        return best.Item1 is not -1;
    }

    /// <summary>Removes the valid times so date scanning never re-reads them
    /// ("19.00" is a time, never day.month).</summary>
    private static string StripTimes(string text)
    {
        text = TimeColon.Replace(text, m =>
            int.Parse(m.Groups[1].Value) <= 23 && int.Parse(m.Groups[2].Value) <= 59 ? " " : m.Value);
        return TimeU.Replace(text, m =>
        {
            var min = m.Groups[2].Success ? int.Parse(m.Groups[2].Value) : 0;
            return int.Parse(m.Groups[1].Value) <= 23 && min <= 59 ? " " : m.Value;
        });
    }

    private static DateTime? FindDay(string date, string time, DateTime today, TimeSpan timeOfDay, TimeSpan nowTime)
    {
        foreach (var field in new[] { date.ToLowerInvariant(), time.ToLowerInvariant() })
        {
            foreach (Match m in NumericDate.Matches(field))
            {
                var day = int.Parse(m.Groups[1].Value);
                var month = int.Parse(m.Groups[2].Value);
                if (month is < 1 or > 12)
                    continue;
                if (m.Groups[3].Success)
                {
                    var year = int.Parse(m.Groups[3].Value);
                    if (day >= 1 && day <= DateTime.DaysInMonth(year, month))
                        return new DateTime(year, month, day);
                }
                else if (day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
                {
                    var candidate = new DateTime(today.Year, month, day);
                    return candidate < today ? candidate.AddYears(1) : candidate;
                }
                else if (month == 2 && day == 29)
                {
                    return NextLeapDay(today); // "29 feb" outside a leap year
                }
            }
            foreach (Match m in DayMonth.Matches(field))
                if (Months.TryGetValue(m.Groups[2].Value, out var month)
                    && MonthDayToDate(int.Parse(m.Groups[1].Value), month,
                        m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null, today) is { } d)
                    return d;
            foreach (Match m in MonthDay.Matches(field))
                if (Months.TryGetValue(m.Groups[1].Value, out var month)
                    && MonthDayToDate(int.Parse(m.Groups[2].Value), month,
                        m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : null, today) is { } d)
                    return d;
        }

        var words = Words(date).Concat(Words(time)).ToList();
        if (words.Any(TodayWords.Contains))
            return today;
        if (words.Any(TomorrowWords.Contains))
            return today.AddDays(1);
        var weekday = words.Select(w => Days.TryGetValue(w, out var d) ? d as DayOfWeek? : null)
            .FirstOrDefault(d => d is not null);
        if (weekday is null)
            return null;
        var delta = ((int)weekday.Value - (int)today.DayOfWeek + 7) % 7;
        if (delta == 0 && timeOfDay <= nowTime)
            delta = 7; // today's name with a passed time means next week
        return today.AddDays(delta);
    }

    private static DateTime? MonthDayToDate(int day, int month, int? year, DateTime today)
    {
        if (year is { } y)
            return day >= 1 && day <= DateTime.DaysInMonth(y, month) ? new DateTime(y, month, day) : null;
        if (day >= 1 && day <= DateTime.DaysInMonth(today.Year, month))
        {
            var candidate = new DateTime(today.Year, month, day);
            return candidate < today ? candidate.AddYears(1) : candidate;
        }
        return month == 2 && day == 29 ? NextLeapDay(today) : null;
    }

    private static DateTime NextLeapDay(DateTime today)
    {
        var year = today.Year;
        while (DateTime.DaysInMonth(year, 2) < 29 || new DateTime(year, 2, 29) < today)
            year++;
        return new DateTime(year, 2, 29);
    }

    private static IEnumerable<string> Words(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"[a-z]+").Select(m => m.Value);

    private static bool IsBlank(string text) => !text.Any(char.IsLetterOrDigit);
}
