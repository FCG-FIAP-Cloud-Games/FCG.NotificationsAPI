using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class NotificationRepository(NotificationsDbContext dbContext) : INotificationRepository
{
    // Consulta com rastreamento (tracking) de propósito: o retry reaproveita a linha
    // e altera o status dela, e o EF precisa enxergar essa alteração no SaveChanges.
    public Task<Notification?> GetByEventAndTypeAsync(
        Guid eventId,
        NotificationType type,
        CancellationToken cancellationToken) =>
        dbContext.Notifications.FirstOrDefaultAsync(
            notification => notification.EventId == eventId && notification.Type == type,
            cancellationToken);

    public void Add(Notification notification) => dbContext.Notifications.Add(notification);
}