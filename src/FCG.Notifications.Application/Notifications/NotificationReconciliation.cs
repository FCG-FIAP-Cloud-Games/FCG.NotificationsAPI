using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Application.Notifications;

public sealed record NotificationReconciliationRequest(
    Guid ActionId, Guid NotificationId, ReconciliationActionType ActionType,
    NotificationStatus ExpectedStatus, DateTimeOffset ExpectedUpdatedAt,
    string OperatorId, string Reason, string? EvidenceReference = null,
    bool DuplicateRiskAcknowledged = false, bool CauseInvestigated = false,
    string? QuiescenceEvidenceReference = null)
{
    public void Validate()
    {
        if (ActionId == Guid.Empty || NotificationId == Guid.Empty) throw new ArgumentException("Identifiers must be stable and nonempty.");
        if (!Enum.IsDefined(ActionType)) throw new ArgumentOutOfRangeException(nameof(ActionType));
        var expected = ActionType == ReconciliationActionType.RescheduleFailure ? NotificationStatus.Failed : NotificationStatus.Unknown;
        if (ExpectedStatus != expected) throw new ArgumentException("Invalid expected state for action.");
        if (ExpectedUpdatedAt == default) throw new ArgumentException("ExpectedUpdatedAt is required.");
        ValidateText(OperatorId, 200, true);
        ValidateText(Reason, 1000, true);
        ValidateText(EvidenceReference, 500, ActionType != ReconciliationActionType.RescheduleFailure);
        ValidateText(QuiescenceEvidenceReference, 500, ActionType == ReconciliationActionType.AuthorizeResend);
        if (ActionType == ReconciliationActionType.AuthorizeResend && !DuplicateRiskAcknowledged)
            throw new ArgumentException("Explicit duplicate risk acknowledgement is required.");
        if (ActionType == ReconciliationActionType.RescheduleFailure && !CauseInvestigated)
            throw new ArgumentException("Investigation of the failure cause must be confirmed.");
    }

    private static void ValidateText(string? value, int maxLength, bool required)
    {
        if (value is null && !required) return;
        if (string.IsNullOrWhiteSpace(value) || value.Length > maxLength || value.Any(char.IsControl))
            throw new ArgumentException("Use bounded single-line administrative metadata; never include credentials or message content.");
    }
}

public sealed record NotificationReconciliationResult(Guid AuditId, Guid ActionId, Guid NotificationId,
    NotificationStatus NewStatus, DateTimeOffset CreatedAt);

public sealed record NotificationReconciliationHistoryItem(Guid AuditId, Guid NotificationId, Guid ActionId,
    ReconciliationActionType ActionType, NotificationStatus PreviousStatus, NotificationStatus NewStatus,
    string OperatorId, string Reason, string? EvidenceReference, DateTimeOffset CreatedAt,
    DateTimeOffset ExpectedUpdatedAt, bool DuplicateRiskAcknowledged, bool CauseInvestigated,
    string? QuiescenceEvidenceReference);

public sealed class NotificationReconciliationConflictException(string message) : InvalidOperationException(message);
