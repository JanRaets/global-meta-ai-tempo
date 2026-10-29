namespace Tempo.Core.Domain;

public enum TimeBucket
{
    Early,     // 05:00–09:00
    Morning,   // 09:00–12:00
    Midday,    // 12:00–14:00
    Afternoon, // 14:00–18:00
    Evening,   // 18:00–05:00
}

public enum TravelDirection
{
    TowardHome,
    AwayFromHome,
    Other,
}

/// <summary>
/// The context in which a habit occurs (design D5): weekday, time bucket, travel direction
/// and optionally the kind of adjacent appointment. Owned by Habit in the database.
/// A null Weekday means "any Monday–Friday" (amended 29 Oct 2026): inference still derives
/// concrete days; the generalised form exists for seeded/edited habits like a weekday-morning
/// coffee stop.
/// </summary>
public sealed record ContextKey(
    DayOfWeek? Weekday,
    TimeBucket TimeBucket,
    TravelDirection Direction,
    string? AdjacentAppointmentKind)
{
    /// <summary>A confirmed habit matches a planning context only when weekday, time bucket and
    /// direction all match and, if the habit has an adjacent appointment kind, that matches too
    /// (habit-learning spec: Habit context matching). A habit without a specific weekday
    /// matches any Monday–Friday planning context.</summary>
    public bool Matches(ContextKey planning) =>
        (Weekday is null
            ? planning.Weekday is >= DayOfWeek.Monday and <= DayOfWeek.Friday
            : Weekday == planning.Weekday)
        && TimeBucket == planning.TimeBucket
        && Direction == planning.Direction
        && (AdjacentAppointmentKind is null
            || string.Equals(AdjacentAppointmentKind, planning.AdjacentAppointmentKind, StringComparison.OrdinalIgnoreCase));

    public static TimeBucket BucketOf(TimeOnly time) => time.Hour switch
    {
        >= 5 and < 9 => TimeBucket.Early,
        >= 9 and < 12 => TimeBucket.Morning,
        >= 12 and < 14 => TimeBucket.Midday,
        >= 14 and < 18 => TimeBucket.Afternoon,
        _ => TimeBucket.Evening,
    };
}
