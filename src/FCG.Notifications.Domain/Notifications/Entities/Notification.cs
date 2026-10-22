using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Domain.Notifications.Entities;

/// <summary>
/// Tentativa lógica de envio de uma notificação.
/// Unicidade: operação + tipo + destinatário lógico + destino SMTP.
/// A mesma linha é reaproveitada em retries; não se cria uma nova a cada tentativa.
/// </summary>
public sealed class Notification
{
    // Construtor sem parâmetros: usado apenas pelo EF Core.
    private Notification()
    {
        Destination = string.Empty;
    }

    public Notification(
        Guid eventId,
        Guid correlationId,
        Guid userId,
        NotificationType type,
        string destination,
        DateTimeOffset createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destination);

        Id = Guid.NewGuid();
        EventId = eventId;
        CorrelationId = correlationId;
        UserId = userId;
        Type = type;
        Destination = destination;
        Status = NotificationStatus.Pending;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        BusinessOperationId = eventId.ToString();
        LogicalRecipientId = userId.ToString();
    }

    public Notification(Guid eventId, Guid correlationId, Guid userId, NotificationType type,
        string businessOperationId, string logicalRecipientId, string smtpDestinationId,
        string destination, string subject, string body, DateTimeOffset createdAt)
        : this(eventId, correlationId, userId, type, destination, createdAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(businessOperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(logicalRecipientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(smtpDestinationId);
        ArgumentNullException.ThrowIfNull(subject);
        ArgumentNullException.ThrowIfNull(body);
        BusinessOperationId = businessOperationId;
        LogicalRecipientId = logicalRecipientId;
        SmtpDestinationId = smtpDestinationId;
        Subject = subject;
        Body = body;
    }

    public string BusinessOperationId { get; private set; } = string.Empty;
    public string LogicalRecipientId { get; private set; } = string.Empty;
    public string SmtpDestinationId { get; private set; } = "legacy";
    // Null identifica registros legados sem conteúdo reconstruível; não são adquiridos.
    public string? Subject { get; private set; }
    public string? Body { get; private set; }
    public Guid? ReservationToken { get; private set; }
    public DateTimeOffset? ReservationExpiresAt { get; private set; }
    public int AttemptCount { get; private set; }
    public DateTimeOffset? NextAttemptAt { get; private set; }
    public string? FailureCode { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public DateTimeOffset? LastAttemptAt { get; private set; }
    public DateTimeOffset? SendingAt { get; private set; }
    public DateTimeOffset? FailedAt { get; private set; }
    public DateTimeOffset? UnknownAt { get; private set; }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public Guid CorrelationId { get; private set; }
    public Guid UserId { get; private set; }
    public NotificationType Type { get; private set; }
    public string Destination { get; private set; }
    public NotificationStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public void MarkAsSent(DateTimeOffset sentAt)
    {
        EnsureLegacyTransition();
        Status = NotificationStatus.Sent;
        SentAt = sentAt;
    }

    public void MarkAsFailed()
    {
        EnsureLegacyTransition();
        Status = NotificationStatus.Failed;
    }

    /// <summary>
    /// Usado quando uma tentativa anterior falhou e o retry vai tentar de novo
    /// reaproveitando a mesma linha.
    /// </summary>
    public void MarkAsPending()
    {
        EnsureLegacyTransition();
        Status = NotificationStatus.Pending;
    }

    private void EnsureLegacyTransition()
    {
        if (Subject is not null || ReservationToken is not null || Status is NotificationStatus.Sent or NotificationStatus.Unknown)
            throw new InvalidOperationException("Entregas persistentes exigem transições condicionais pelo repositório.");
    }
}
