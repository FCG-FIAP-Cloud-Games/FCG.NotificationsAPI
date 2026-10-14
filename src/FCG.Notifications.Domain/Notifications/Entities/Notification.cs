using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Domain.Notifications.Entities;

/// <summary>
/// Tentativa lógica de envio de uma notificação.
/// Unicidade lógica: EventId + Type (definida no banco, na configuração do EF).
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
    }

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
        Status = NotificationStatus.Sent;
        SentAt = sentAt;
    }

    public void MarkAsFailed() => Status = NotificationStatus.Failed;

    /// <summary>
    /// Usado quando uma tentativa anterior falhou e o retry vai tentar de novo
    /// reaproveitando a mesma linha.
    /// </summary>
    public void MarkAsPending() => Status = NotificationStatus.Pending;
}