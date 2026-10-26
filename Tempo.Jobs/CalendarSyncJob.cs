namespace Tempo.Jobs;

/// <summary>Hangfire entry point: every 5 minutes and on demand (POST /sync/calendar).</summary>
public sealed class CalendarSyncJob(CalendarSyncService sync)
{
    public Task RunAsync(CancellationToken ct) => sync.SyncAsync(ct);
}
