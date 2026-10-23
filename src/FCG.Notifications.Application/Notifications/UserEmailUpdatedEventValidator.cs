using System.Net.Mail;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public static class UserEmailUpdatedEventValidator
{
    public static bool IsValid(UserEmailUpdatedEvent @event) =>
        @event.EventId != Guid.Empty &&
        @event.CorrelationId != Guid.Empty &&
        @event.UserId != Guid.Empty &&
        MailAddress.TryCreate(@event.Email, out _);
}
