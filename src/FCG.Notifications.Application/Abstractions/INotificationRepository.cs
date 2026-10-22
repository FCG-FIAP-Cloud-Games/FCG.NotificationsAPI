using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Application.Abstractions;

public interface INotificationRepository
{
    Task<List<Guid>> GetEligibleIdsAsync(int limit, CancellationToken cancellationToken);
    Task<Notification?> TryAcquireAsync(Guid id, TimeSpan lease, CancellationToken cancellationToken);
    Task<bool> TryStartSendingAsync(Guid id, Guid token, CancellationToken cancellationToken);
    Task<bool> TryMarkSentAsync(Guid id, Guid token, CancellationToken cancellationToken);
    Task<bool> TryMarkFailedAsync(Guid id, Guid token, string failureCode, DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken);
    Task<bool> TryMarkUnknownAsync(Guid id, Guid token, string failureCode, CancellationToken cancellationToken);
    Task<int> RecoverExpiredAsync(CancellationToken cancellationToken);

    Task<Notification?> GetByEventAndTypeAsync(
        Guid eventId, 
        NotificationType type, 
        CancellationToken cancellationToken);

    void Add(Notification notification);
}
