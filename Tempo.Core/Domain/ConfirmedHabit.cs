namespace Tempo.Core.Domain;

/// <summary>
/// The only habit shape the planner accepts (design D3). The constructor is internal and is
/// invoked only by <see cref="ConfirmHabitHandler"/>; the agent cannot confirm a habit, only
/// the user can, and every confirmation path (notification action, app button, voice intent)
/// runs through that handler. A test asserts there is no public constructor.
/// </summary>
public sealed class ConfirmedHabit
{
    internal ConfirmedHabit(Guid habitId, Guid placeId, string placeName, double avgDurationMin, ContextKey context, double lat, double lng, HabitKind kind)
    {
        HabitId = habitId;
        PlaceId = placeId;
        PlaceName = placeName;
        AvgDurationMin = avgDurationMin;
        Context = context;
        Lat = lat;
        Lng = lng;
        Kind = kind;
    }

    public Guid HabitId { get; }
    /// <summary>Stop joins the route; ReturnsHome/StaysPut decide a look-ahead origin
    /// (chained-origin amendment, 30 Oct 2026) and never enter stop subsets.</summary>
    public HabitKind Kind { get; }
    public Guid PlaceId { get; }
    public string PlaceName { get; }
    public double AvgDurationMin { get; }
    public ContextKey Context { get; }
    public double Lat { get; }
    public double Lng { get; }

    public bool Matches(ContextKey planning) => Context.Matches(planning);
}
