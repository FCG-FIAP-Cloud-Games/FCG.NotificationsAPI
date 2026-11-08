using FCG.Notifications.Application.Abstractions;
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
        if (userId == Guid.Empty)
            throw new ArgumentException("UserId must not be empty.", nameof(userId));
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        var normalizedEmail = email.Trim();

        // Empate: vence o maior endereço na collation C, independentemente da ordem de chegada.
        // Não infere cronologia entre eventos de mesma data. Preserva casing e normalização existentes.
        // Usa a transação do contexto, se houver; não chama SaveChanges nem faz commit independente.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_email_contacts ("UserId", "Email", "UpdatedAt")
            VALUES ({userId}, {normalizedEmail}, {updatedAt.ToUniversalTime()})
            ON CONFLICT ("UserId") DO UPDATE
            SET "Email" = EXCLUDED."Email", "UpdatedAt" = EXCLUDED."UpdatedAt"
            WHERE EXCLUDED."UpdatedAt" > user_email_contacts."UpdatedAt"
               OR (EXCLUDED."UpdatedAt" = user_email_contacts."UpdatedAt"
                   AND EXCLUDED."Email" COLLATE "C" > user_email_contacts."Email" COLLATE "C")
            """, cancellationToken);
    }
}
