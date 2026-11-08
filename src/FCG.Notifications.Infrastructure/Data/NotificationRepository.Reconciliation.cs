using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed partial class NotificationRepository
{
    public async Task<NotificationReconciliationResult> ReconcileAsync(NotificationReconciliationRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        request.Validate();
        if (dbContext.Database.CurrentTransaction is not null)
            throw new InvalidOperationException("Reconciliation owns its transaction; do not nest it in another unit of work.");
        request = request with { ExpectedUpdatedAt = request.ExpectedUpdatedAt.ToUniversalTime() };
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(request))));
        await using var transaction = await dbContext.Database.BeginTransactionAsync(cancellationToken);
        var actionKey = $"notification-reconciliation:{request.ActionId:D}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({actionKey}, 0))", cancellationToken);
        var previous = await dbContext.NotificationReconciliationAudits.AsNoTracking()
            .SingleOrDefaultAsync(a => a.ActionId == request.ActionId, cancellationToken);
        if (previous is not null)
        {
            if (previous.RequestHash != hash)
                throw new NotificationReconciliationConflictException("ActionId already used with different parameters.");
            await transaction.CommitAsync(cancellationToken);
            return Result(previous);
        }

        // Serialize different actions on this delivery, including conditional dispatcher UPDATEs.
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM notifications WHERE \"Id\" = {request.NotificationId} FOR UPDATE", cancellationToken);
        var delivery = await dbContext.Notifications.AsNoTracking().Where(n => n.Id == request.NotificationId)
            .Select(n => new { n.Status, n.UpdatedAt, n.ReservationToken, n.ReservationExpiresAt,
                Reconstructable = n.Subject != null && n.Body != null }).SingleOrDefaultAsync(cancellationToken);
        if (delivery is null) throw new KeyNotFoundException("Notification not found.");
        if (delivery.Status != request.ExpectedStatus || delivery.UpdatedAt != request.ExpectedUpdatedAt)
            throw new NotificationReconciliationConflictException("Notification changed; obtain a fresh operational snapshot.");
        // Refuse ANY residual reservation, even expired: expiration is not evidence that SMTP stopped.
        if (delivery.ReservationToken.HasValue || delivery.ReservationExpiresAt.HasValue)
            throw new NotificationReconciliationConflictException("Notification has a reservation.");
        if (!delivery.Reconstructable)
            throw new NotificationReconciliationConflictException("Legacy notification has no persistent delivery content.");

        // The absence of a trusted guard blocks Unknown -> Pending in production by default.
        await using var quiescence = request.ActionType == ReconciliationActionType.AuthorizeResend
            ? await (replaySafety ?? throw new InvalidOperationException("Unknown replay blocked: no trusted dispatcher quiescence integration configured."))
                .AcquireQuiescenceAsync(request.QuiescenceEvidenceReference!, cancellationToken)
                ?? throw new InvalidOperationException("No quiescence lease was granted.")
            : null;
        try
        {
            var newStatus = request.ActionType == ReconciliationActionType.ConfirmAcceptance ? NotificationStatus.Sent : NotificationStatus.Pending;
            var changed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE notifications SET "Status" = {newStatus.ToString()},
                    "UpdatedAt" = GREATEST(clock_timestamp(), "UpdatedAt" + interval '1 microsecond'),
                    "SentAt" = CASE WHEN {newStatus.ToString()} = 'Sent' THEN clock_timestamp() ELSE "SentAt" END,
                    "NextAttemptAt" = NULL, "ReservationToken" = NULL, "ReservationExpiresAt" = NULL
                WHERE "Id" = {request.NotificationId} AND "Status" = {request.ExpectedStatus.ToString()}
                    AND "UpdatedAt" = {request.ExpectedUpdatedAt}
                    AND "ReservationToken" IS NULL AND "ReservationExpiresAt" IS NULL
                """, cancellationToken);
            if (changed != 1) throw new NotificationReconciliationConflictException("Conditional transition rejected.");

            var auditId = Guid.NewGuid();
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notification_reconciliation_audits
                    ("AuditId", "NotificationId", "ActionId", "ActionType", "PreviousStatus", "NewStatus",
                     "OperatorId", "Reason", "EvidenceReference", "CreatedAt", "RequestHash", "ExpectedUpdatedAt",
                     "DuplicateRiskAcknowledged", "CauseInvestigated", "QuiescenceEvidenceReference")
                VALUES ({auditId}, {request.NotificationId}, {request.ActionId}, {request.ActionType.ToString()},
                    {request.ExpectedStatus.ToString()}, {newStatus.ToString()}, {request.OperatorId}, {request.Reason},
                    {request.EvidenceReference}, clock_timestamp(), {hash}, {request.ExpectedUpdatedAt},
                    {request.DuplicateRiskAcknowledged}, {request.CauseInvestigated}, {request.QuiescenceEvidenceReference})
                """, cancellationToken);
            var audit = await dbContext.NotificationReconciliationAudits.AsNoTracking()
                .SingleAsync(a => a.AuditId == auditId, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return Result(audit);
        }
        catch
        {
            // Keep the quiescence lease until rollback completes, not merely until an UPDATE fails.
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    public async Task<IReadOnlyList<NotificationReconciliationHistoryItem>> GetHistoryAsync(Guid notificationId,
        int page, int pageSize, CancellationToken cancellationToken)
    {
        new NotificationOperationsFilter(Page: page, PageSize: pageSize).Validate();
        return await dbContext.NotificationReconciliationAudits.AsNoTracking().Where(a => a.NotificationId == notificationId)
            .OrderByDescending(a => a.CreatedAt).ThenByDescending(a => a.AuditId)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(a => new NotificationReconciliationHistoryItem(a.AuditId, a.NotificationId, a.ActionId,
                a.ActionType, a.PreviousStatus, a.NewStatus, a.OperatorId, a.Reason, a.EvidenceReference, a.CreatedAt,
                a.ExpectedUpdatedAt, a.DuplicateRiskAcknowledged, a.CauseInvestigated, a.QuiescenceEvidenceReference))
            .ToListAsync(cancellationToken);
    }

    private static NotificationReconciliationResult Result(NotificationReconciliationAudit audit) =>
        new(audit.AuditId, audit.ActionId, audit.NotificationId, audit.NewStatus, audit.CreatedAt);
}
