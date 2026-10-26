using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Tempo.Core.Calendar;

namespace Tempo.Integrations.Google;

/// <summary>
/// Read-only Google Calendar access over raw HTTP. Only the events list call exists;
/// no write method is implemented anywhere (calendar-sync spec: No calendar writes),
/// and a reflection test pins that.
/// </summary>
public sealed class GoogleCalendarClient(HttpClient http, GoogleOAuthService oauth, GoogleTokenStore tokenStore) : ICalendarClient
{
    public async Task<bool> IsConnectedAsync(CancellationToken ct = default) =>
        await tokenStore.GetRefreshTokenAsync(ct) is not null;

    public async Task<IReadOnlyList<CalendarEvent>> ListEventsAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var accessToken = await oauth.GetAccessTokenAsync(ct);
        var url = "calendar/v3/calendars/primary/events"
            + $"?timeMin={Uri.EscapeDataString(from.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}"
            + $"&timeMax={Uri.EscapeDataString(to.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'"))}"
            + "&singleEvents=true&orderBy=startTime&maxResults=250";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new HttpRequestException($"Calendar list failed ({(int)response.StatusCode}): {body[..Math.Min(body.Length, 300)]}");
        }
        var parsed = await response.Content.ReadFromJsonAsync<WireEventList>(cancellationToken: ct)
            ?? throw new HttpRequestException("Calendar list returned an empty body");

        return (parsed.Items ?? [])
            .Where(i => i.Status != "cancelled" && i.Id is not null)
            .Where(i => i.Start?.DateTime is not null && i.End?.DateTime is not null) // all-day events carry only a date
            .Select(i => new CalendarEvent(
                i.Id!,
                string.IsNullOrWhiteSpace(i.Summary) ? "(untitled)" : i.Summary!,
                i.Start!.DateTime!.Value,
                i.End!.DateTime!.Value,
                string.IsNullOrWhiteSpace(i.Location) ? null : i.Location,
                i.ConferenceData is not null || i.HangoutLink is not null,
                IsAllDay: false))
            .ToList();
    }

    private sealed record WireEventList([property: JsonPropertyName("items")] List<WireEvent>? Items);

    private sealed record WireEvent(
        [property: JsonPropertyName("id")] string? Id,
        [property: JsonPropertyName("status")] string? Status,
        [property: JsonPropertyName("summary")] string? Summary,
        [property: JsonPropertyName("location")] string? Location,
        [property: JsonPropertyName("start")] WireEventTime? Start,
        [property: JsonPropertyName("end")] WireEventTime? End,
        [property: JsonPropertyName("conferenceData")] object? ConferenceData,
        [property: JsonPropertyName("hangoutLink")] string? HangoutLink);

    private sealed record WireEventTime(
        [property: JsonPropertyName("dateTime")] DateTimeOffset? DateTime,
        [property: JsonPropertyName("date")] string? Date);
}
