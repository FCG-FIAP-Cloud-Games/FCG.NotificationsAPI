using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FCG.Notifications.Infrastructure.Data.Configurations;

public sealed class UserEmailContactConfiguration : IEntityTypeConfiguration<UserEmailContact>
{
    public void Configure(EntityTypeBuilder<UserEmailContact> builder)
    {
        builder.ToTable("user_email_contacts");
        builder.HasKey(contact => contact.UserId);
        builder.Property(contact => contact.Email).HasMaxLength(320).IsRequired();
        builder.Property(contact => contact.UpdatedAt).IsRequired();
    }
}
