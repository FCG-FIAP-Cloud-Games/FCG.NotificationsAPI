namespace FCG.Notifications.Application.Abstractions;

public interface INotificationSender
{
    Task SendAsync(NotificationMessage message, CancellationToken cancellationToken);
}