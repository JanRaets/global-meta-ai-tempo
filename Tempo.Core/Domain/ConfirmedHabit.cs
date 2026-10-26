namespace Tempo.Core.Domain;

/// <summary>
/// The only habit shape the planner accepts (design D3). The constructor is internal and is
/// invoked only by <see cref="ConfirmHabitHandler"/>; the agent cannot confirm a habit, only
/// the user can, and every confirmation path (Telegram button, app button, voice intent)
/// runs through that handler. A test asserts there is no public constructor.
/// </summary>
public sealed class ConfirmedHabit
{
    internal ConfirmedHabit(Guid habitId, Guid placeId, string placeName, double avgDurationMin, ContextKey context, double lat, double lng)
    {
        HabitId = habitId;
        PlaceId = placeId;
        PlaceName = placeName;
        AvgDurationMin = avgDurationMin;
        Context = context;
        Lat = lat;
        Lng = lng;
    }

    public Guid HabitId { get; }
    public Guid PlaceId { get; }
    public string PlaceName { get; }
    public double AvgDurationMin { get; }
    public ContextKey Context { get; }
    public double Lat { get; }
    public double Lng { get; }

    public bool Matches(ContextKey planning) => Context.Matches(planning);
}
