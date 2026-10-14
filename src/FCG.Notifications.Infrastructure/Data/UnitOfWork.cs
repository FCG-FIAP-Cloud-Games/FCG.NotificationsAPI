using FCG.Notifications.Application.Abstractions;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class UnitOfWork(NotificationsDbContext dbContext) : IUnitOfWork
{
    public Task SaveChangesAsync(CancellationToken cancellationToken) =>
        dbContext.SaveChangesAsync(cancellationToken);
}