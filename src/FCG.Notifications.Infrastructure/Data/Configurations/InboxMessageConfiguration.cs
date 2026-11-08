using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Notifications.Infrastructure.Data.Configurations;

public sealed class InboxMessageConfiguration : IEntityTypeConfiguration<InboxMessage>
{
    public void Configure(EntityTypeBuilder<InboxMessage> builder)
    {
        builder.ToTable("inbox_messages");

        builder.HasKey(message => message.Id);

        builder.Property(message => message.ConsumerName)
            .IsRequired()
            .HasMaxLength(200);

        builder.Property(message => message.EventId)
            .IsRequired();

        builder.Property(message => message.ProcessedAt)
            .IsRequired();

        // Unicidade de ConsumerName + EventId
        builder.HasIndex(message => new { message.EventId, message.ConsumerName })
            .IsUnique();
    }
}