using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Core.Models;

namespace Tempo.Agent;

/// <summary>What the vision model returns: the TEXT it read, never a resolved date (D1).
/// dateText/timeText are literal ("Thursday", "do 30 okt", "19:00"); only
/// <see cref="AppointmentDateResolver"/> turns them into a DateTime.</summary>
public sealed class CapturedAppointment
{
    public string Title { get; set; } = "";
    public string DateText { get; set; } = "";
    public string TimeText { get; set; } = "";
    public string PlaceText { get; set; } = "";
    public double Confidence { get; set; }
    public string RawText { get; set; } = "";
}

/// <summary>What the confirm card shows. Start is null when the words could not be
/// resolved — the card's time input then starts empty for the user to correct.
/// DateText/TimeText echo the literal words the model read (for the live report and
/// for tracing why a start did or did not resolve).</summary>
public sealed record CaptureResult(
    string Title,
    DateTimeOffset? Start,
    string? StartLocal,
    string DateText,
    string TimeText,
    string PlaceText,
    string? PlaceName,
    bool HasLocation,
    double Confidence,
    string RawText,
    bool Found);

public sealed record ConfirmCapture(string Title, string StartLocal, string? PlaceText);

public sealed record ConfirmedCapture(Guid AppointmentId, string Title, DateTimeOffset Start, string? PlaceName);

/// <summary>User-facing capture failure (bad image, missing time): the endpoint maps it to 400.</summary>
public sealed class CaptureValidationException(string message) : Exception(message);

/// <summary>
/// Photo → appointment (design D10, capture-appointment spec): Spark vision reads the
/// text, <see cref="AppointmentDateResolver"/> resolves it in the user timezone, the
/// place goes through the calendar-sync path (<see cref="PlaceResolver"/>, 50 m reuse).
/// Confirm creates an <see cref="Appointment"/> with <c>Source = Captured</c> — never
/// written to Google Calendar — and raises <see cref="AppointmentChanged"/> exactly
/// like calendar sync, so the agent plans for it.
/// </summary>
public sealed class CaptureService(
    TempoDbContext db,
    IMetaModels models,
    PlaceResolver places,
    IGeocoder geocoder,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<CaptureService> logger)
{
    /// <summary>Client-side resize aims under 500 KB; the server cap is generous.</summary>
    public const int MaxImageBytes = 5_000_000;

    public static readonly string[] AllowedContentTypes = ["image/jpeg", "image/png"];

    // Strict-mode schema (additionalProperties:false + all required ⇒ strict:true on the
    // wire): lax mode occasionally corrupted a property key, strict decoding never does.
    private const string Schema = """
        {"type":"object","additionalProperties":false,"properties":{
          "title":{"type":"string"},
          "dateText":{"type":"string"},
          "timeText":{"type":"string"},
          "placeText":{"type":"string"},
          "confidence":{"type":"number"},
          "rawText":{"type":"string"}},
         "required":["title","dateText","timeText","placeText","confidence","rawText"]}
        """;

    private const string Instruction = """
        Read the appointment in this photo — a message, letter, invitation or calendar event.
        Return a JSON object with exactly these fields:
        - title: the short event title as written.
        - dateText: the date words EXACTLY as written ("Thursday", "do 30 okt", "30/10"). Never a resolved calendar date.
        - timeText: the time EXACTLY as written ("19:00", "19u30"). Nothing else.
        - placeText: the venue or address as written.
        - confidence: 0 to 1, how sure you are this photo shows an appointment.
        - rawText: all relevant text you read, in one string.
        If no appointment is visible, return empty strings and confidence 0. Copy text literally: do not translate, do not resolve dates, do not add anything you did not read.
        """;

    public async Task<CaptureResult> CaptureAsync(
        ReadOnlyMemory<byte> image, string contentType, CancellationToken ct = default)
    {
        if (!AllowedContentTypes.Contains(contentType.ToLowerInvariant()))
            throw new CaptureValidationException("Send a JPEG or PNG photo.");
        if (image.Length == 0)
            throw new CaptureValidationException("Empty photo.");
        if (image.Length > MaxImageBytes)
            throw new CaptureValidationException("Photo too large (max 5 MB).");

        var captured = (await models.DescribeImageAsync<CapturedAppointment>(
            image, contentType, Instruction, Schema, purpose: "capture-appointment", ct)).Value;

        var tz = await UserTimeZoneAsync(ct);
        var start = AppointmentDateResolver.Resolve(
            captured.DateText, captured.TimeText, clock.GetUtcNow(), tz);
        var found = !string.IsNullOrWhiteSpace(captured.Title)
            || !string.IsNullOrWhiteSpace(captured.DateText)
            || !string.IsNullOrWhiteSpace(captured.TimeText)
            || !string.IsNullOrWhiteSpace(captured.PlaceText);

        string? placeName = null;
        var hasLocation = false;
        if (!string.IsNullOrWhiteSpace(captured.PlaceText))
        {
            var geo = await TryGeocodeAsync(captured.PlaceText.Trim(), ct);
            if (geo is not null)
            {
                placeName = geo.Label ?? captured.PlaceText.Trim();
                hasLocation = true;
            }
        }

        return new CaptureResult(
            Title: FirstNonBlank(captured.Title, FirstLine(captured.RawText), "Captured appointment"),
            Start: start,
            StartLocal: start is null ? null
                : TimeZoneInfo.ConvertTime(start.Value, tz).ToString("yyyy-MM-ddTHH:mm", CultureInfo.InvariantCulture),
            DateText: captured.DateText ?? "",
            TimeText: captured.TimeText ?? "",
            PlaceText: captured.PlaceText ?? "",
            PlaceName: placeName,
            HasLocation: hasLocation,
            Confidence: double.IsNaN(captured.Confidence) ? 0 : Math.Clamp(captured.Confidence, 0, 1),
            RawText: captured.RawText ?? "",
            Found: found);
    }

    public async Task<ConfirmedCapture> ConfirmAsync(ConfirmCapture req, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(req.Title))
            throw new CaptureValidationException("Add a title first.");
        if (!DateTime.TryParseExact(req.StartLocal, "yyyy-MM-ddTHH:mm",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
            throw new CaptureValidationException("Pick a date and time.");
        var tz = await UserTimeZoneAsync(ct);
        var start = new DateTimeOffset(local, tz.GetUtcOffset(local));

        Guid? placeId = null;
        string? placeName = null;
        if (!string.IsNullOrWhiteSpace(req.PlaceText))
        {
            try
            {
                placeId = await places.ResolveAsync(req.PlaceText.Trim(), ct); // the calendar path
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("Capture confirm: geocoding '{Place}' failed ({Message}); creating placeless",
                    req.PlaceText.Trim(), ex.Message);
            }
            if (placeId is not null)
                placeName = (await db.Places.FindAsync([placeId.Value], ct))?.Name;
        }

        var appointment = new Appointment
        {
            Id = Guid.NewGuid(),
            Source = AppointmentSource.Captured,
            Title = req.Title.Trim(),
            Start = start,
            End = start.AddHours(1),
            RawLocation = string.IsNullOrWhiteSpace(req.PlaceText) ? null : req.PlaceText.Trim(),
            PlaceId = placeId,
            IsVirtual = false,
        };
        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);
        await events.PublishAsync(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Created), ct);
        logger.LogInformation("Captured appointment '{Title}' at {Start} (place: {Place})",
            appointment.Title, start, placeName ?? "none");
        return new ConfirmedCapture(appointment.Id, appointment.Title, start, placeName);
    }

    /// <summary>A failed place preview degrades to "no location", never to a failed capture.</summary>
    private async Task<GeocodeResult?> TryGeocodeAsync(string placeText, CancellationToken ct)
    {
        try
        {
            return await geocoder.GeocodeAsync(placeText, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Capture: geocoding '{Place}' failed ({Message}); showing without location",
                placeText, ex.Message);
            return null;
        }
    }

    private async Task<TimeZoneInfo> UserTimeZoneAsync(CancellationToken ct)
    {
        var id = (await db.UserSettings.AsNoTracking().FirstOrDefaultAsync(ct))?.TimeZone ?? "Europe/Amsterdam";
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning("Unknown timezone '{Id}', falling back to Europe/Amsterdam", id);
            return TimeZoneInfo.FindSystemTimeZoneById("Europe/Amsterdam");
        }
    }

    private static string FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c))?.Trim() ?? "";

    private static string FirstLine(string? text) =>
        text?.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim() is { } line
            ? line[..Math.Min(line.Length, 80)] : "";
}
