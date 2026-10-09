using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Notifications.Infrastructure.Data.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications");

        builder.HasKey(notification => notification.Id);

        builder.Property(notification => notification.EventId).IsRequired();
        builder.Property(notification => notification.CorrelationId).IsRequired();
        builder.Property(notification => notification.UserId).IsRequired();

        // Enums gravados como texto: legível no banco e imune a reordenação do enum.
        builder.Property(notification => notification.Type)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(notification => notification.Status)
            .HasConversion<string>()
            .HasMaxLength(50)
            .IsRequired();

        builder.Property(notification => notification.Destination)
            .IsRequired()
            .HasMaxLength(320);

        builder.Property(notification => notification.CreatedAt).IsRequired();

        // Unicidade lógica (Card 28, item 4): uma notificação por EventId + Type.
        builder.HasIndex(notification => new { notification.EventId, notification.Type })
            .IsUnique();
    }
}