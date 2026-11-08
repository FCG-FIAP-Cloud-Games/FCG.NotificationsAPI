using FCG.Notifications.Application.Notifications;

namespace FCG.Notifications.Application.Abstractions;

/// <summary>Internal administrative read model. Expose only behind administrative authorization.</summary>
public interface INotificationOperationsQuery
{
    Task<NotificationOperationsPage> SearchAsync(NotificationOperationsFilter filter, CancellationToken cancellationToken);
    Task<NotificationOperationsItem?> GetAsync(Guid notificationId, CancellationToken cancellationToken);
    Task<IReadOnlyList<NotificationReconciliationHistoryItem>> GetHistoryAsync(Guid notificationId,
        int page, int pageSize, CancellationToken cancellationToken);
}
