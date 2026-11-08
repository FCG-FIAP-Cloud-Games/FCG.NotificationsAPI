using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Domain.Notifications.Entities;

public enum ReconciliationActionType { ConfirmAcceptance, AuthorizeResend, RescheduleFailure }

/// <summary>Append-only decision record. PostgreSQL rejects updates and deletes.</summary>
public sealed class NotificationReconciliationAudit
{
    public Guid AuditId { get; private set; }
    public Guid NotificationId { get; private set; }
    public Guid ActionId { get; private set; }
    public ReconciliationActionType ActionType { get; private set; }
    public NotificationStatus PreviousStatus { get; private set; }
    public NotificationStatus NewStatus { get; private set; }
    public string OperatorId { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public string? EvidenceReference { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public string RequestHash { get; private set; } = string.Empty;
    public DateTimeOffset ExpectedUpdatedAt { get; private set; }
    public bool DuplicateRiskAcknowledged { get; private set; }
    public bool CauseInvestigated { get; private set; }
    public string? QuiescenceEvidenceReference { get; private set; }
}
