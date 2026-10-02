namespace FCG.Notifications.Application.Messaging.Contracts;

public sealed record PaymentProcessedEvent
(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid PaymentId,
    Guid OrderId,
    Guid UserId,
    decimal Amount,
    string Currency,
    PaymentStatus Status
) : IEventEnvolope;

public enum PaymentStatus
{
    Pending,
    Approved,
    Rejected
}