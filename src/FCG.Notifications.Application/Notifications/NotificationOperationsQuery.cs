using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Application.Notifications;

/// <summary>
/// CreatedAt interval: inclusive From, exclusive Until. Exact operation/destination filters.
/// Pages are ordered by CreatedAt DESC, Id DESC; concurrent changes do not form a snapshot.
/// </summary>
public sealed record NotificationOperationsFilter(
    NotificationStatus? Status = null,
    DateTimeOffset? CreatedFrom = null,
    DateTimeOffset? CreatedUntil = null,
    string? BusinessOperationId = null,
    string? SmtpDestinationId = null,
    int Page = 1,
    int PageSize = 50)
{
    public void Validate()
    {
        if (Status.HasValue && !Enum.IsDefined(Status.Value))
            throw new ArgumentOutOfRangeException(nameof(Status));
        if (PageSize is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(PageSize));
        if (Page < 1 || (long)(Page - 1) * PageSize > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(Page));
        if (CreatedFrom.HasValue && CreatedUntil.HasValue && CreatedFrom >= CreatedUntil)
            throw new ArgumentException("CreatedUntil must be after CreatedFrom.");
        if (BusinessOperationId is not null && (string.IsNullOrWhiteSpace(BusinessOperationId) || BusinessOperationId.Length > 200))
            throw new ArgumentException("Invalid operation identifier.", nameof(BusinessOperationId));
        if (SmtpDestinationId is not null && (string.IsNullOrWhiteSpace(SmtpDestinationId) || SmtpDestinationId.Length > 100))
            throw new ArgumentException("Invalid SMTP destination identifier.", nameof(SmtpDestinationId));
    }
}

// Explicit allowlist: no recipient, content, credentials or reservation ownership token.
public sealed record NotificationOperationsItem(
    Guid NotificationId, NotificationStatus Status, Guid EventId, Guid CorrelationId,
    string BusinessOperationId, NotificationType Type, string SmtpDestinationId, int AttemptCount,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? LastAttemptAt,
    DateTimeOffset? NextAttemptAt, DateTimeOffset? ReservationExpiresAt, DateTimeOffset? SendingAt,
    DateTimeOffset? SentAt, DateTimeOffset? FailedAt, DateTimeOffset? UnknownAt, string? FailureCode);

public sealed record NotificationOperationsPage(
    IReadOnlyList<NotificationOperationsItem> Items, int Page, int PageSize, bool HasNextPage);
