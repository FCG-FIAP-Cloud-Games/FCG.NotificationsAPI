using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using Microsoft.EntityFrameworkCore;

namespace FCG.Notifications.Infrastructure.Data;

public sealed class NotificationRepository(NotificationsDbContext dbContext) : INotificationRepository
{
<<<<<<< Updated upstream
    public async Task<bool> TryAddAsync(Notification notification, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(notification);
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Persistir intenção exige transação explícita.");
        if (notification.Status != NotificationStatus.Pending || notification.Subject is null || notification.Body is null)
            throw new InvalidOperationException("A intenção deve estar Pending e conter assunto e corpo.");
        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications ("Id", "EventId", "CorrelationId", "UserId", "Type", "Destination", "Status",
                "CreatedAt", "UpdatedAt", "BusinessOperationId", "LogicalRecipientId", "SmtpDestinationId", "Subject", "Body", "AttemptCount")
            VALUES ({notification.Id}, {notification.EventId}, {notification.CorrelationId}, {notification.UserId},
                {notification.Type.ToString()}, {notification.Destination}, 'Pending', {notification.CreatedAt}, {notification.UpdatedAt},
                {notification.BusinessOperationId}, {notification.LogicalRecipientId}, {notification.SmtpDestinationId},
                {notification.Subject}, {notification.Body}, 0)
            ON CONFLICT ("BusinessOperationId", "Type", "LogicalRecipientId", "SmtpDestinationId") DO NOTHING
            """, cancellationToken) == 1;
=======
    public async Task<List<Notification>> LockBusinessOperationAsync(string operationId, NotificationType type, CancellationToken cancellationToken)
    {
        if (dbContext.Database.CurrentTransaction is null)
            throw new InvalidOperationException("Reserva da operação de negócio exige transação explícita.");
        var key = $"notifications:{type}:{operationId}";
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))", cancellationToken);
        return await dbContext.Notifications.AsNoTracking()
            .Where(n => n.BusinessOperationId == operationId && n.Type == type).ToListAsync(cancellationToken);
>>>>>>> Stashed changes
    }

    public Task<List<Guid>> GetEligibleIdsAsync(int limit, CancellationToken cancellationToken)
    {
        if (limit is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(limit));
        return dbContext.Notifications.FromSqlInterpolated($"""
            SELECT * FROM notifications
            WHERE "Subject" IS NOT NULL AND "Body" IS NOT NULL
              AND (("Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= clock_timestamp()))
                OR ("Status" = 'Failed' AND "NextAttemptAt" <= clock_timestamp()))
            ORDER BY "CreatedAt", "Id" LIMIT {limit}
            """).AsNoTracking().Select(n => n.Id).ToListAsync(cancellationToken);
    }

    // SQL parametrizado usa o relógio do PostgreSQL. As operações não usam entidades rastreadas.
    public async Task<Notification?> TryAcquireAsync(Guid id, TimeSpan lease, CancellationToken cancellationToken)
    {
        if (lease <= TimeSpan.Zero || lease > TimeSpan.FromHours(1))
            throw new ArgumentOutOfRangeException(nameof(lease));
        var token = Guid.NewGuid();
        var changed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET "Status" = 'Preparing', "ReservationToken" = {token},
                "ReservationExpiresAt" = clock_timestamp() + {lease},
                "AttemptCount" = "AttemptCount" + 1, "LastAttemptAt" = clock_timestamp(),
                "UpdatedAt" = clock_timestamp(), "NextAttemptAt" = NULL
            WHERE "Id" = {id} AND "Subject" IS NOT NULL AND "Body" IS NOT NULL
              AND (("Status" = 'Pending' AND ("NextAttemptAt" IS NULL OR "NextAttemptAt" <= clock_timestamp()))
                OR ("Status" = 'Failed' AND "NextAttemptAt" <= clock_timestamp()))
            """, cancellationToken);
        return changed == 0 ? null : await dbContext.Notifications.AsNoTracking()
            .SingleOrDefaultAsync(n => n.Id == id && n.ReservationToken == token, cancellationToken);
    }

    public async Task<bool> TryStartSendingAsync(Guid id, Guid token, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET "Status" = 'Sending', "SendingAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {id} AND "ReservationToken" = {token} AND "Status" = 'Preparing'
              AND "ReservationExpiresAt" > clock_timestamp()
            """, cancellationToken) == 1;

    public async Task<bool> TryMarkSentAsync(Guid id, Guid token, CancellationToken cancellationToken) =>
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET "Status" = 'Sent', "SentAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(),
                "ReservationToken" = NULL, "ReservationExpiresAt" = NULL, "FailureCode" = NULL
            WHERE "Id" = {id} AND "ReservationToken" = {token} AND "Status" = 'Sending'
              AND "ReservationExpiresAt" > clock_timestamp()
            """, cancellationToken) == 1;

    // Só Preparing pode falhar com reenvio seguro. Exceções durante SMTP são Unknown.
    public async Task<bool> TryMarkFailedAsync(Guid id, Guid token, string failureCode,
        DateTimeOffset? nextAttemptAt, CancellationToken cancellationToken)
    {
        ValidateFailureCode(failureCode);
        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET "Status" = 'Failed', "FailedAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(),
                "FailureCode" = {failureCode}, "NextAttemptAt" = {nextAttemptAt},
                "ReservationToken" = NULL, "ReservationExpiresAt" = NULL
            WHERE "Id" = {id} AND "ReservationToken" = {token} AND "Status" = 'Preparing'
              AND "ReservationExpiresAt" > clock_timestamp()
            """, cancellationToken) == 1;
    }

    public async Task<bool> TryMarkUnknownAsync(Guid id, Guid token, string failureCode, CancellationToken cancellationToken)
    {
        ValidateFailureCode(failureCode);
        return await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE notifications SET "Status" = 'Unknown', "UnknownAt" = clock_timestamp(), "UpdatedAt" = clock_timestamp(),
                "FailureCode" = {failureCode}, "NextAttemptAt" = NULL,
                "ReservationToken" = NULL, "ReservationExpiresAt" = NULL
            WHERE "Id" = {id} AND "ReservationToken" = {token} AND "Status" = 'Sending'
              AND "ReservationExpiresAt" > clock_timestamp()
            """, cancellationToken) == 1;
    }

    public Task<int> RecoverExpiredAsync(CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlRawAsync("""
            UPDATE notifications SET
                "UnknownAt" = CASE WHEN "Status" = 'Sending' THEN clock_timestamp() ELSE "UnknownAt" END,
                "FailureCode" = CASE WHEN "Status" = 'Sending' THEN 'SEND_LEASE_EXPIRED' ELSE 'PREPARATION_LEASE_EXPIRED' END,
                "Status" = CASE WHEN "Status" = 'Sending' THEN 'Unknown' ELSE 'Pending' END,
                "ReservationToken" = NULL, "ReservationExpiresAt" = NULL,
                "NextAttemptAt" = NULL, "UpdatedAt" = clock_timestamp()
            WHERE "Status" IN ('Preparing', 'Sending') AND "ReservationExpiresAt" <= clock_timestamp()
            """, cancellationToken);

    private static void ValidateFailureCode(string code)
    {
        // Somente códigos de máquina; nunca exception.Message, resposta SMTP ou destinatário.
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        if (code.Length > 64 || code.Any(c => !(c is >= 'A' and <= 'Z' or >= '0' and <= '9' or '_')))
            throw new ArgumentException("Use um código de falha de até 64 caracteres A-Z, 0-9 ou underscore.", nameof(code));
    }

    // Consulta com rastreamento (tracking) de propósito: o retry reaproveita a linha
    // e altera o status dela, e o EF precisa enxergar essa alteração no SaveChanges.
    public Task<Notification?> GetByEventAndTypeAsync(
        Guid eventId,
        NotificationType type,
        CancellationToken cancellationToken) =>
        dbContext.Notifications.FirstOrDefaultAsync(
            notification => notification.EventId == eventId && notification.Type == type,
            cancellationToken);

    public void Add(Notification notification) => dbContext.Notifications.Add(notification);
}
