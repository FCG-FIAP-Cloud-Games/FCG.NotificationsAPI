namespace FCG.Notifications.Application.Messaging.Contracts;

public interface IEventEnvolope
{
    Guid EventId { get; }
    Guid CorrelationId { get; }
    DateTimeOffset OccurredAt { get; }
    int Version { get; }
}