using Tempo.Core.Calendar;
using Tempo.Core.Geo;

namespace Tempo.Tests.Fakes;

public sealed class FakeCalendarClient : ICalendarClient
{
    public bool Connected { get; set; } = true;
    public List<CalendarEvent> Events { get; } = [];

    public Task<bool> IsConnectedAsync(CancellationToken ct = default) => Task.FromResult(Connected);

    public Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<CalendarEvent>>(Events.Where(e => e.Start >= from && e.Start <= to).ToList());
}

/// <summary>Geocodes only the texts it was taught; everything else is unresolvable.</summary>
public sealed class FakeGeocoder : IGeocoder
{
    private readonly Dictionary<string, GeocodeResult> known = new(StringComparer.OrdinalIgnoreCase);

    public FakeGeocoder Knows(string text, double lat, double lng, string? label = null)
    {
        known[text] = new GeocodeResult(lat, lng, label ?? text);
        return this;
    }

    public Task<GeocodeResult?> GeocodeAsync(string text, CancellationToken ct = default) =>
        Task.FromResult(known.TryGetValue(text, out var result) ? result : null);

    public string ReverseLabel { get; set; } = "Somewhere 1, Utrecht";

    public Task<string?> ReverseAsync(double lat, double lng, CancellationToken ct = default) =>
        Task.FromResult<string?>(ReverseLabel);
}
