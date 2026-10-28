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

    public List<(string Text, GeoPoint? Focus)> Lookups { get; } = [];

    public Task<GeocodeResult?> GeocodeAsync(string text, GeoPoint? focus = null, CancellationToken ct = default)
    {
        Lookups.Add((text, focus));
        return Task.FromResult(known.TryGetValue(text, out var result) ? result : null);
    }

    private readonly Dictionary<string, List<GeoCandidate>> knownCandidates = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<GeoCandidate>> knownUnfocused = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Candidate list returned for a text (any focus, unless an unfocused list exists).</summary>
    public FakeGeocoder KnowsCandidates(string text, params GeoCandidate[] candidates)
    {
        knownCandidates[text] = candidates.ToList();
        return this;
    }

    /// <summary>Candidate list returned only when searched WITHOUT a focus point (rule c).</summary>
    public FakeGeocoder KnowsCandidatesUnfocused(string text, params GeoCandidate[] candidates)
    {
        knownUnfocused[text] = candidates.ToList();
        return this;
    }

    public Task<IReadOnlyList<GeoCandidate>> SearchAsync(string text, GeoPoint? focus = null, CancellationToken ct = default)
    {
        Lookups.Add((text, focus));
        if (focus is null && knownUnfocused.TryGetValue(text, out var unfocused))
            return Task.FromResult<IReadOnlyList<GeoCandidate>>(unfocused);
        if (knownCandidates.TryGetValue(text, out var list))
            return Task.FromResult<IReadOnlyList<GeoCandidate>>(list);
        if (known.TryGetValue(text, out var single))
            return Task.FromResult<IReadOnlyList<GeoCandidate>>(
                [new GeoCandidate(single.Lat, single.Lng, single.Label, single.Label, null)]);
        return Task.FromResult<IReadOnlyList<GeoCandidate>>([]);
    }

    public string ReverseLabel { get; set; } = "Somewhere 1, Utrecht";

    public Task<string?> ReverseAsync(double lat, double lng, CancellationToken ct = default) =>
        Task.FromResult<string?>(ReverseLabel);
}
