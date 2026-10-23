namespace FCG.Notifications.Application.Messaging.Contracts;

/// <summary>
/// Published by UsersAPI when a user's email address changes.
/// </summary>
public sealed record UserEmailUpdatedEvent(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid UserId,
    string Email) : IEventEnvelope;
