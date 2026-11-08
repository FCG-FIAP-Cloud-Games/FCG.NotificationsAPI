namespace FCG.Notifications.Domain.Notifications.Entities;

/// <summary>
/// Notifications-owned projection of the user's current email address.
/// </summary>
public sealed class UserEmailContact
{
    private UserEmailContact()
    {
        Email = string.Empty;
    }

    public UserEmailContact(Guid userId, string email, DateTimeOffset updatedAt)
    {
        if (userId == Guid.Empty)
        {
            throw new ArgumentException("UserId must not be empty.", nameof(userId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        UserId = userId;
        Email = email.Trim();
        UpdatedAt = updatedAt;
    }

    public Guid UserId { get; private set; }
    public string Email { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public void UpdateEmail(string email, DateTimeOffset updatedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        Email = email.Trim();
        UpdatedAt = updatedAt;
    }
}
