using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Tempo.Agent;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.Capture;

/// <summary>Capture-appointment spec via the scripted fake: fixed extracted fields in,
/// resolved start and place preview out; confirm creates the appointment and raises
/// the event exactly like calendar sync. Reference now: Tue 27 Oct 2026 10:00 Amsterdam.</summary>
public sealed class CaptureServiceTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeTimeProvider clock = new(DateTimeOffset.Parse("2026-10-27T09:00:00Z"));
    private readonly ScriptedMetaModels model = new();
    private readonly FakeGeocoder geocoder = new FakeGeocoder()
        .Knows("De Jonge Haan", 52.0907, 5.1214, "De Jonge Haan, Utrecht");
    private readonly CollectingDomainEvents events = new();

    public CaptureServiceTests()
    {
        db.Context.UserSettings.Add(new UserSettings { Id = Guid.NewGuid(), LocationApiKey = "k" });
        db.Context.SaveChanges();
    }

    private CaptureService Service() => new(db.Context, model,
        new PlaceResolver(db.Context, geocoder), geocoder, events, clock,
        NullLogger<CaptureService>.Instance);

    private static CapturedAppointment Extract(
        string title = "Dinner", string date = "Thursday", string time = "19:00",
        string place = "De Jonge Haan", double confidence = 0.9, string raw = "Dinner Thursday 19:00 at De Jonge Haan") =>
        new() { Title = title, DateText = date, TimeText = time, PlaceText = place, Confidence = confidence, RawText = raw };

    [Fact]
    public async Task Extract_returns_resolved_start_and_place_preview()
    {
        model.EnqueueTyped(Extract());

        var result = await Service().CaptureAsync(new byte[] { 1, 2, 3 }, "image/jpeg");

        Assert.True(result.Found);
        Assert.Equal("Dinner", result.Title);
        Assert.Equal("Thursday", result.DateText);
        Assert.Equal("19:00", result.TimeText);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 19, 0, 0, TimeSpan.FromHours(1)), result.Start);
        Assert.Equal("2026-10-29T19:00", result.StartLocal);
        Assert.True(result.HasLocation);
        Assert.Equal("De Jonge Haan, Utrecht", result.PlaceName);
        Assert.Equal(0.9, result.Confidence);
        Assert.Empty(db.Context.Appointments); // extraction persists nothing
        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task Extract_with_unresolvable_date_leaves_start_empty_for_manual_entry()
    {
        model.EnqueueTyped(Extract(title: "Odd one", date: "someday"));

        var result = await Service().CaptureAsync(new byte[] { 1 }, "image/png");

        Assert.True(result.Found);
        Assert.Null(result.Start);
        Assert.Null(result.StartLocal);
    }

    [Fact]
    public async Task No_appointment_found_offers_what_was_read()
    {
        model.EnqueueTyped(Extract(title: "", date: "", time: "", place: "", confidence: 0, raw: "hello"));

        var result = await Service().CaptureAsync(new byte[] { 1 }, "image/jpeg");

        Assert.False(result.Found);
        Assert.Null(result.Start);
        Assert.False(result.HasLocation);
        Assert.Equal("hello", result.RawText);
    }

    [Fact]
    public async Task Low_confidence_is_returned_not_blocked()
    {
        model.EnqueueTyped(Extract(confidence: 0.3));

        var result = await Service().CaptureAsync(new byte[] { 1 }, "image/jpeg");

        Assert.True(result.Found);
        Assert.Equal(0.3, result.Confidence);
        var confirmed = await Service().ConfirmAsync(
            new ConfirmCapture(result.Title, result.StartLocal!, result.PlaceText));
        Assert.Equal("Dinner", confirmed.Title); // the card's Add still works
        Assert.Single(events.Published.OfType<AppointmentChanged>());
    }

    [Fact]
    public async Task No_place_path_shows_without_location_and_confirms_placeless()
    {
        model.EnqueueTyped(Extract(place: "Somewhere over the rainbow"));

        var result = await Service().CaptureAsync(new byte[] { 1 }, "image/jpeg");

        Assert.False(result.HasLocation);
        Assert.Null(result.PlaceName);
        var confirmed = await Service().ConfirmAsync(
            new ConfirmCapture(result.Title, result.StartLocal!, result.PlaceText));
        var appointment = db.Context.Appointments.Single(a => a.Id == confirmed.AppointmentId);
        Assert.Null(appointment.PlaceId);
        Assert.Equal("Somewhere over the rainbow", appointment.RawLocation);
        Assert.Null(confirmed.PlaceName);
    }

    [Fact]
    public async Task Confirm_creates_captured_appointment_and_raises_event_with_place_reuse()
    {
        var existing = new Place { Id = Guid.NewGuid(), Name = "De Jonge Haan, Utrecht", Lat = 52.0907, Lng = 5.1214 };
        db.Context.Places.Add(existing);
        await db.Context.SaveChangesAsync();

        var confirmed = await Service().ConfirmAsync(
            new ConfirmCapture("Dinner", "2026-10-29T19:00", "De Jonge Haan"));

        var appointment = db.Context.Appointments.Single(a => a.Id == confirmed.AppointmentId);
        Assert.Equal(AppointmentSource.Captured, appointment.Source);
        Assert.Null(appointment.ExternalId);
        Assert.Equal("Dinner", appointment.Title);
        Assert.Equal(new DateTimeOffset(2026, 10, 29, 19, 0, 0, TimeSpan.FromHours(1)), appointment.Start);
        Assert.Equal(appointment.Start.AddHours(1), appointment.End);
        Assert.Equal(5, appointment.BufferMin); // the entity default
        Assert.False(appointment.IsVirtual);
        Assert.Equal(existing.Id, appointment.PlaceId); // the existing 50 m reuse
        Assert.Single(db.Context.Places); // no duplicate place
        var change = Assert.Single(events.Published.OfType<AppointmentChanged>());
        Assert.Equal(AppointmentChangeKind.Created, change.Kind);
        Assert.Equal(appointment.Id, change.AppointmentId);
    }

    [Fact]
    public async Task Confirm_rejects_blank_title()
    {
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().ConfirmAsync(new ConfirmCapture("  ", "2026-10-29T19:00", null)));
        Assert.Empty(db.Context.Appointments);
    }

    [Fact]
    public async Task Confirm_rejects_unparseable_time()
    {
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().ConfirmAsync(new ConfirmCapture("Dinner", "next Thursday", null)));
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().ConfirmAsync(new ConfirmCapture("Dinner", "", null)));
        Assert.Empty(db.Context.Appointments);
    }

    [Fact]
    public async Task Extract_rejects_non_image_without_a_model_call()
    {
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().CaptureAsync(new byte[] { 1 }, "image/gif"));
    }

    [Fact]
    public async Task Extract_rejects_empty_and_oversized_bodies()
    {
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().CaptureAsync(Array.Empty<byte>(), "image/jpeg"));
        await Assert.ThrowsAsync<CaptureValidationException>(() =>
            Service().CaptureAsync(new byte[CaptureService.MaxImageBytes + 1], "image/jpeg"));
    }

    [Fact]
    public async Task Null_model_strings_are_tolerated()
    {
        model.EnqueueTyped(new CapturedAppointment
        {
            Title = null!, DateText = null!, TimeText = null!,
            PlaceText = null!, RawText = null!, Confidence = 0,
        });

        var result = await Service().CaptureAsync(new byte[] { 1 }, "image/jpeg");

        Assert.False(result.Found);
        Assert.Equal("Captured appointment", result.Title);
        Assert.Equal("", result.PlaceText);
    }

    public void Dispose() => db.Dispose();
}
