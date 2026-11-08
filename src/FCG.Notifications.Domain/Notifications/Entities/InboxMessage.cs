namespace FCG.Notifications.Domain.Notifications.Entities;

/// <summary>
/// Registro persistente de um evento já processado por um consumer (Card 28/#71).
/// A unicidade de ConsumerName + EventId é garantida no banco, não só no código.
/// </summary>
public sealed class InboxMessage
{
    // Construtor sem parâmetros: usado apenas pelo EF Core.
    private InboxMessage()
    {
        ConsumerName = string.Empty;
    }

    public InboxMessage(Guid eventId, string consumerName, DateTimeOffset processedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(consumerName);

        Id = Guid.NewGuid();
        EventId = eventId;
        ConsumerName = consumerName;
        ProcessedAt = processedAt;
    }

    public Guid Id { get; private set; }
    public Guid EventId { get; private set; }
    public string ConsumerName { get; private set; }
    public DateTimeOffset ProcessedAt { get; private set; }
}