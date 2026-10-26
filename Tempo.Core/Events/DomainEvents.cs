using Microsoft.Extensions.Logging;

namespace Tempo.Core.Events;

public interface IDomainEvent;

public enum AppointmentChangeKind
{
    Created,
    Updated,
    Deleted,
}

public sealed record AppointmentChanged(Guid AppointmentId, AppointmentChangeKind Kind) : IDomainEvent;

public sealed record VisitCreated(Guid VisitId) : IDomainEvent;

public sealed record HabitConfirmed(Guid HabitId) : IDomainEvent;

/// <summary>In-process domain events. Section 7 subscribes the agent loop to these;
/// until then the logging implementation just records that they fired.</summary>
public interface IDomainEvents
{
    Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent;
}

public sealed class LoggingDomainEvents(ILogger<LoggingDomainEvents> logger) : IDomainEvents
{
    public Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent
    {
        logger.LogInformation("Domain event {Event}: {Payload}", typeof(T).Name, domainEvent);
        return Task.CompletedTask;
    }
}
