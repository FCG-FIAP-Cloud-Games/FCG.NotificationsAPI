using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class InboxStore(NotificationsDbContext dbContext) : IInboxStore
{
    public Task<bool> ExistsAsync(Guid eventId, string consumerName, CancellationToken cancellationToken) =>
        dbContext.InboxMessages.AnyAsync(
            message => message.EventId == eventId && message.ConsumerName == consumerName,
            cancellationToken);
    
    public void Add(InboxMessage message) => dbContext.InboxMessages.Add(message);
    
}