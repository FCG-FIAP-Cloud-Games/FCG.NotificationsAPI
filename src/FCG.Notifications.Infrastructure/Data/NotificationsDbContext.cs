using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public DbSet<InboxMessage> InboxMessages => Set<InboxMessage>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<NotificationReconciliationAudit> NotificationReconciliationAudits => Set<NotificationReconciliationAudit>();
    public DbSet<UserEmailContact> UserEmailContacts => Set<UserEmailContact>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
    }
}
