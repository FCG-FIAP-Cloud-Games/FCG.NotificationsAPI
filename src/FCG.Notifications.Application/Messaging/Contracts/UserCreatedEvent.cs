namespace FCG.Notifications.Application.Messaging.Contracts;

public sealed record UserCreatedEvent
(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid UserId,
    string Name, 
    string Email   
) : IEventEnvelope;