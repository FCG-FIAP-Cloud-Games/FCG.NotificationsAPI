using System.Collections.Concurrent;
using FCG.Notifications.Application.Abstractions;

namespace FCG.Notifications.UnitTests.TestDoubles;

public sealed class InMemoryUserEmailRepository : IUserEmailRepository
{
    private readonly ConcurrentDictionary<Guid, (string Email, DateTimeOffset UpdatedAt)> _emails = new();

    public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult(_emails.TryGetValue(userId, out var contact) ? contact.Email : null);

    public Task UpsertAsync(Guid userId, string email, DateTimeOffset updatedAt, CancellationToken cancellationToken)
    {
        _emails.AddOrUpdate(
            userId,
            (email, updatedAt),
            (_, current) => updatedAt >= current.UpdatedAt ? (email, updatedAt) : current);
        return Task.CompletedTask;
    }
}
