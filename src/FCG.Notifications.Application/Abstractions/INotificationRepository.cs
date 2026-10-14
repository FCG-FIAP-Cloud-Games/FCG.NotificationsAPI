using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Application.Abstractions;

public interface INotificationRepository
{
    Task<Notification?> GetByEventAndTypeAsync(
        Guid eventId, 
        NotificationType type, 
        CancellationToken cancellationToken);

    void Add(Notification notification);
}