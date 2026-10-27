using Hangfire;
using Microsoft.Extensions.Logging;
using Tempo.Agent;
using Tempo.Core.Events;

namespace Tempo.Api;

/// <summary>Domain events wake the agent (design D7 triggers) via a background job,
/// so publishers never wait on a model call.</summary>
public sealed class HangfireDomainEvents(IBackgroundJobClient jobs, ILogger<HangfireDomainEvents> logger) : IDomainEvents
{
    public Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent
    {
        logger.LogInformation("Domain event {Event}: {Payload}", typeof(T).Name, domainEvent);
        switch (domainEvent)
        {
            case AppointmentChanged { Kind: not AppointmentChangeKind.Deleted } changed:
                jobs.Enqueue<AgentRunJob>(j => j.RunAsync("event:appointment-changed", changed.AppointmentId, CancellationToken.None));
                break;
            case VisitCreated:
                jobs.Enqueue<AgentRunJob>(j => j.RunAsync("event:visit-created", null, CancellationToken.None));
                break;
            case HabitConfirmed:
                jobs.Enqueue<AgentRunJob>(j => j.RunAsync("event:habit-confirmed", null, CancellationToken.None));
                break;
            case IntentApplied intent:
                jobs.Enqueue<AgentRunJob>(j => j.RunAsync($"intent:{intent.Kind}", intent.AppointmentId, CancellationToken.None));
                break;
        }
        return Task.CompletedTask;
    }
}
