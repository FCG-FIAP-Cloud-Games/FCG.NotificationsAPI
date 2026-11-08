using System.Net.Mail;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public static class PurchaseNotificationTestEventValidator
{
    public static bool IsValid(PurchaseNotificationTestEvent @event) =>
        @event.EventId != Guid.Empty &&
        @event.CorrelationId != Guid.Empty &&
        @event.UserId != Guid.Empty &&
        @event.OrderId != Guid.Empty &&
        @event.GameId != Guid.Empty &&
        @event.Amount >= 0 &&
        !string.IsNullOrWhiteSpace(@event.Currency) &&
        MailAddress.TryCreate(@event.Email, out _);
}
