using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class UserEmailRepository(NotificationsDbContext dbContext) : IUserEmailRepository
{
    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        dbContext.UserEmailContacts
            .Where(contact => contact.UserId == userId)
            .Select(contact => contact.Email)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task UpsertAsync(
        Guid userId,
        string email,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken)
    {
        var contact = await dbContext.UserEmailContacts.FindAsync([userId], cancellationToken);
        if (contact is null)
        {
            dbContext.UserEmailContacts.Add(new UserEmailContact(userId, email, updatedAt));
        }
        else if (updatedAt >= contact.UpdatedAt)
        {
            contact.UpdateEmail(email, updatedAt);
        }

        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
