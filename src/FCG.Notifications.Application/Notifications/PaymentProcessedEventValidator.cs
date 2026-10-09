using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public static class PaymentProcessedEventValidator
{
    public static bool IsValid(PaymentProcessedEvent @event) =>
        @event.EventId != Guid.Empty &&
        @event.CorrelationId != Guid.Empty &&
        @event.PaymentId != Guid.Empty &&
        @event.OrderId != Guid.Empty &&
        @event.UserId != Guid.Empty &&
        @event.GameId != Guid.Empty &&
        Enum.IsDefined(@event.Status);
}