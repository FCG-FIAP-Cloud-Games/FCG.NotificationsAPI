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
        builder.Property(n => n.BusinessOperationId).HasMaxLength(200).IsRequired();
        builder.Property(n => n.LogicalRecipientId).HasMaxLength(200).IsRequired();
        builder.Property(n => n.SmtpDestinationId).HasMaxLength(100).IsRequired();
        builder.Property(n => n.Subject).HasMaxLength(998);
        builder.Property(n => n.FailureCode).HasMaxLength(64);
        builder.Property(n => n.ReservationToken).IsConcurrencyToken();
        builder.Property(n => n.Status).IsConcurrencyToken();
        builder.HasIndex(n => new { n.BusinessOperationId, n.Type, n.LogicalRecipientId, n.SmtpDestinationId })
            .IsUnique();
        builder.HasIndex(n => new { n.Status, n.NextAttemptAt });
        builder.HasIndex(n => new { n.Status, n.ReservationExpiresAt });
        builder.HasIndex(n => new { n.EventId, n.Type });
    }
}
