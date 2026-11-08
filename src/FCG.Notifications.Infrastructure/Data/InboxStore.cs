using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class InboxStore(NotificationsDbContext dbContext) : IInboxStore
{
    // A reserva só tem significado junto aos efeitos persistidos na mesma transação.
    public async Task<bool> TryReserveAsync(Guid eventId, string consumerName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("A reserva Inbox exige transação explícita.");
        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO inbox_messages ("Id", "EventId", "ConsumerName", "ProcessedAt")
            VALUES ({Guid.NewGuid()}, {eventId}, {consumerName}, clock_timestamp())
            ON CONFLICT ("EventId", "ConsumerName") DO NOTHING
            """, cancellationToken) == 1;
    }

    public Task<bool> ExistsAsync(Guid eventId, string consumerName, CancellationToken cancellationToken) =>
        dbContext.InboxMessages.AnyAsync(
            message => message.EventId == eventId && message.ConsumerName == consumerName,
            cancellationToken);
    
    public void Add(InboxMessage message) => dbContext.InboxMessages.Add(message);
    
}
