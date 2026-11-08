namespace FCG.Notifications.Application.Messaging.Contracts;

public interface IEventEnvelope
{
    Guid EventId { get; }
    Guid CorrelationId { get; }
    DateTimeOffset OccurredAt { get; }
    int Version { get; }
}