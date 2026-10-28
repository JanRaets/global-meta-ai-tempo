using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Tempo.Core.Calendar;
using Tempo.Core.Data;
using Tempo.Core.Domain;
using Tempo.Core.Events;
using Tempo.Core.Geo;

namespace Tempo.Jobs;

/// <summary>
/// Syncs Google Calendar events in [now − 1h, now + 7d] into Appointment records
/// (calendar-sync spec). Captured appointments are never touched here.
/// </summary>
public sealed class CalendarSyncService(
    TempoDbContext db,
    ICalendarClient calendar,
    PlaceResolver places,
    IDomainEvents events,
    TimeProvider clock,
    ILogger<CalendarSyncService> logger)
{
    public async Task SyncAsync(CancellationToken ct = default)
    {
        if (!await calendar.IsConnectedAsync(ct))
        {
            logger.LogInformation("Calendar sync skipped: Google not connected");
            return;
        }

        var now = clock.GetUtcNow();
        var from = now.AddHours(-1);
        var to = now.AddDays(7);
        var fetched = (await calendar.ListEventsAsync(from, to, ct))
            .Where(e => !e.IsAllDay)
            .ToDictionary(e => e.Id);
        var existing = await db.Appointments
            .Where(a => a.Source == AppointmentSource.Synced)
            .ToListAsync(ct);
        var changes = new List<AppointmentChanged>();

        foreach (var ev in fetched.Values)
        {
            var appointment = existing.FirstOrDefault(a => a.ExternalId == ev.Id);
            if (appointment is null)
            {
                appointment = new Appointment
                {
                    Id = Guid.NewGuid(),
                    Source = AppointmentSource.Synced,
                    ExternalId = ev.Id,
                    Title = ev.Title,
                    // ToUniversalTime: Google sends local offsets (+01:00); Npgsql rejects
                    // any non-UTC DateTimeOffset (same fix as capture confirm and the seeds).
                    Start = ev.Start.ToUniversalTime(),
                    End = ev.End.ToUniversalTime(),
                    RawLocation = ev.Location,
                    PlaceId = await places.ResolveAsync(ev.Location, ct),
                    IsVirtual = ev.Location is null && ev.HasConferenceLink,
                };
                db.Appointments.Add(appointment);
                changes.Add(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Created));
            }
            else if (appointment.Start != ev.Start || appointment.End != ev.End
                || appointment.Title != ev.Title || appointment.RawLocation != ev.Location)
            {
                if (appointment.RawLocation != ev.Location)
                {
                    appointment.PlaceId = await places.ResolveAsync(ev.Location, ct);
                    appointment.IsVirtual = ev.Location is null && ev.HasConferenceLink;
                }
                appointment.Title = ev.Title;
                appointment.Start = ev.Start.ToUniversalTime();
                appointment.End = ev.End.ToUniversalTime();
                appointment.RawLocation = ev.Location;
                appointment.UpdatedAt = now;
                changes.Add(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Updated));
            }
        }

        // Gone from the calendar (deleted or cancelled) within the sync window → remove,
        // and cancel any pending nudge for it (calendar-sync spec: Event deleted).
        foreach (var appointment in existing.Where(a =>
            a.Start >= from && a.Start <= to && a.ExternalId is not null && !fetched.ContainsKey(a.ExternalId)))
        {
            var pendingNudges = await db.Nudges
                .Where(n => n.AppointmentId == appointment.Id && n.Status == NudgeStatus.Pending)
                .ToListAsync(ct);
            foreach (var nudge in pendingNudges)
                nudge.Status = NudgeStatus.Cancelled;
            db.Appointments.Remove(appointment); // plans cascade-delete
            changes.Add(new AppointmentChanged(appointment.Id, AppointmentChangeKind.Deleted));
        }

        await db.SaveChangesAsync(ct);
        foreach (var change in changes)
            await events.PublishAsync(change, ct);
        if (changes.Count > 0)
            logger.LogInformation("Calendar sync applied {Count} change(s)", changes.Count);
    }
}
