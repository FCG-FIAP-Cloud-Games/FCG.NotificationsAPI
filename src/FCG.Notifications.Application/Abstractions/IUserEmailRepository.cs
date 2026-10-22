namespace FCG.Notifications.Application.Abstractions;

public interface IUserEmailRepository
{
    Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken);

    Task UpsertAsync(Guid userId, string email, DateTimeOffset updatedAt, CancellationToken cancellationToken);
}
