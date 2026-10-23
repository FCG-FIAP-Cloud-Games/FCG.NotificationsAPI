using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Notifications.Infrastructure.Data.Configurations;

public sealed class NotificationReconciliationAuditConfiguration : IEntityTypeConfiguration<NotificationReconciliationAudit>
{
    public void Configure(EntityTypeBuilder<NotificationReconciliationAudit> builder)
    {
        builder.ToTable("notification_reconciliation_audits");
        builder.HasKey(a => a.AuditId);
        builder.HasIndex(a => a.ActionId).IsUnique();
        builder.HasIndex(a => new { a.NotificationId, a.CreatedAt, a.AuditId });
        builder.HasOne<Notification>().WithMany().HasForeignKey(a => a.NotificationId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(a => a.ActionType).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.PreviousStatus).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.NewStatus).HasConversion<string>().HasMaxLength(50);
        builder.Property(a => a.OperatorId).HasMaxLength(200).IsRequired();
        builder.Property(a => a.Reason).HasMaxLength(1000).IsRequired();
        builder.Property(a => a.EvidenceReference).HasMaxLength(500);
        builder.Property(a => a.QuiescenceEvidenceReference).HasMaxLength(500);
        builder.Property(a => a.RequestHash).HasMaxLength(64).IsRequired();
    }
}
