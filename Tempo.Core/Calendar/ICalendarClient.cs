namespace Tempo.Core.Calendar;

public sealed record CalendarEvent(
    string Id,
    string Title,
    DateTimeOffset Start,
    DateTimeOffset End,
    string? Location,
    bool HasConferenceLink,
    bool IsAllDay);

/// <summary>Read-only by construction: no write method exists anywhere on this surface
/// (calendar-sync spec: No calendar writes).</summary>
public interface ICalendarClient
{
    Task<bool> IsConnectedAsync(CancellationToken ct = default);
    Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default);
}
