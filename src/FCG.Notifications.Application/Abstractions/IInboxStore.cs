using FCG.Notifications.Domain.Notifications.Entities;

namespace FCG.Notifications.Application.Abstractions;

public interface IInboxStore
{
    Task<bool> ExistsAsync(Guid eventId, string consumerName, CancellationToken cancellationToken);

    void Add(InboxMessage message);
}