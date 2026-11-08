namespace FCG.Notifications.Application.Messaging.Contracts;

/// <summary>
/// Development-only message for testing purchase email delivery with a
/// recipient supplied in RabbitMQ Management. This is not a PaymentsAPI event.
/// </summary>
public sealed record PurchaseNotificationTestEvent(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid UserId,
    Guid OrderId,
    Guid GameId,
    decimal Amount,
    string Currency,
    string Email);
