using Tempo.Core.Events;

namespace Tempo.Tests.Fakes;

public sealed class CollectingDomainEvents : IDomainEvents
{
    public List<IDomainEvent> Published { get; } = [];

    public Task PublishAsync<T>(T domainEvent, CancellationToken ct = default) where T : IDomainEvent
    {
        Published.Add(domainEvent);
        return Task.CompletedTask;
    }
}
