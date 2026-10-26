using Microsoft.Extensions.Logging.Abstractions;
using Tempo.Core.Calendar;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;
using Tempo.Jobs;
using Tempo.Tests.Fakes;

namespace Tempo.Tests.CalendarSync;

public sealed class CalendarSyncServiceTests : IDisposable
{
    private readonly TestDb db = new();
    private readonly FakeCalendarClient calendar = new();
    private readonly FakeGeocoder geocoder = new FakeGeocoder()
        .Knows("Dentist, Utrecht", 52.0907, 5.1214)
        .Knows("Dentist around the corner, Utrecht", 52.09095, 5.12145); // ~30 m away

    private readonly CollectingDomainEvents events = new();
    private readonly DateTimeOffset now = DateTimeOffset.UtcNow;

    private CalendarSyncService Service() => new(
        db.Context, calendar, new PlaceResolver(db.Context, geocoder), events,
        TimeProvider.System, NullLogger<CalendarSyncService>.Instance);

    private CalendarEvent Event(string id = "ev1", string title = "Dentist", int startInHours = 2,
        string? location = "Dentist, Utrecht", bool conference = false, bool allDay = false) =>
        new(id, title, now.AddHours(startInHours), now.AddHours(startInHours + 1), location, conference, allDay);

    [Fact]
    public async Task New_event_with_location_creates_an_appointment_with_resolved_place()
    {
        calendar.Events.Add(Event());

        await Service().SyncAsync();

        var appointment = Assert.Single(db.Context.Appointments);
        Assert.Equal("Dentist", appointment.Title);
        Assert.Equal("ev1", appointment.ExternalId);
        Assert.Equal(AppointmentSource.Synced, appointment.Source);
        Assert.NotNull(appointment.PlaceId);
        Assert.False(appointment.IsVirtual);
        var change = Assert.Single(events.Published.OfType<AppointmentChanged>());
        Assert.Equal(AppointmentChangeKind.Created, change.Kind);
    }

    [Fact]
    public async Task Moved_event_updates_the_appointment_and_raises_AppointmentChanged()
    {
        calendar.Events.Add(Event());
        await Service().SyncAsync();
        events.Published.Clear();

        calendar.Events[0] = Event(startInHours: 4);
        await Service().SyncAsync();

        var appointment = Assert.Single(db.Context.Appointments);
        Assert.Equal(now.AddHours(4), appointment.Start);
        var change = Assert.Single(events.Published.OfType<AppointmentChanged>());
        Assert.Equal(AppointmentChangeKind.Updated, change.Kind);
    }

    [Fact]
    public async Task Deleted_event_removes_the_appointment_and_cancels_its_pending_nudge()
    {
        calendar.Events.Add(Event());
        await Service().SyncAsync();
        var appointment = Assert.Single(db.Context.Appointments);
        db.Context.Nudges.Add(new Nudge
        {
            Id = Guid.NewGuid(),
            AppointmentId = appointment.Id,
            PlanId = Guid.NewGuid(),
            PlanVersion = 1,
            Kind = NudgeKind.Leave,
            Text = "Leave at 09:37 by bike",
        });
        await db.Context.SaveChangesAsync();
        events.Published.Clear();

        calendar.Events.Clear();
        await Service().SyncAsync();

        Assert.Empty(db.Context.Appointments);
        Assert.Equal(NudgeStatus.Cancelled, Assert.Single(db.Context.Nudges).Status);
        var change = Assert.Single(events.Published.OfType<AppointmentChanged>());
        Assert.Equal(AppointmentChangeKind.Deleted, change.Kind);
    }

    [Fact]
    public async Task Unresolvable_location_stores_the_appointment_without_a_place()
    {
        calendar.Events.Add(Event(location: "??? nowhere ???"));

        await Service().SyncAsync();

        var appointment = Assert.Single(db.Context.Appointments);
        Assert.Null(appointment.PlaceId);
        Assert.False(appointment.IsVirtual);
    }

    [Fact]
    public async Task Conference_event_without_location_is_virtual()
    {
        calendar.Events.Add(Event(location: null, conference: true));

        await Service().SyncAsync();

        var appointment = Assert.Single(db.Context.Appointments);
        Assert.Null(appointment.PlaceId);
        Assert.True(appointment.IsVirtual);
    }

    [Fact]
    public async Task Nearby_locations_within_50m_reuse_the_same_place()
    {
        calendar.Events.Add(Event(id: "ev1", location: "Dentist, Utrecht"));
        calendar.Events.Add(Event(id: "ev2", title: "Follow-up", startInHours: 5, location: "Dentist around the corner, Utrecht"));

        await Service().SyncAsync();

        Assert.Single(db.Context.Places);
        Assert.Equal(2, db.Context.Appointments.Count());
        Assert.Single(db.Context.Appointments.Select(a => a.PlaceId).Distinct());
    }

    [Fact]
    public async Task All_day_events_are_skipped()
    {
        calendar.Events.Add(Event(allDay: true));

        await Service().SyncAsync();

        Assert.Empty(db.Context.Appointments);
    }

    [Fact]
    public async Task Unchanged_event_produces_no_change_event()
    {
        calendar.Events.Add(Event());
        await Service().SyncAsync();
        events.Published.Clear();

        await Service().SyncAsync();

        Assert.Empty(events.Published);
    }

    [Fact]
    public async Task Not_connected_syncs_nothing()
    {
        calendar.Connected = false;
        calendar.Events.Add(Event());

        await Service().SyncAsync();

        Assert.Empty(db.Context.Appointments);
    }

    public void Dispose() => db.Dispose();
}
