using System.Text.Json.Serialization;

namespace FCG.Notifications.Application.Messaging.Contracts;

/// <summary>
/// Producer: PaymentsAPI. Consumers: CatalogAPI e NotificationsAPI (filas independentes).
/// Contrato definido no Card 05 (#48). Representa apenas o resultado financeiro —
/// não implica que a concessão do jogo na biblioteca já foi concluída pelo CatalogAPI.
/// Um pagamento e um resultado terminal por OrderId na primeira versão.
/// </summary>
public sealed record PaymentProcessedEvent
(
    Guid EventId,
    Guid CorrelationId,
    DateTimeOffset OccurredAt,
    int Version,
    Guid PaymentId,
    Guid OrderId,
    Guid UserId,
    Guid GameId,
    decimal Amount,
    string Currency,
    PaymentStatus Status
) : IEventEnvelope;

/// <summary>
/// Valores definidos no Card 05 (#48). PaymentsAPI publica como string
/// ("Approved"/"Rejected") — o conversor abaixo garante a desserialização
/// correta independente da configuração global do serializer.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaymentStatus
{
    Approved,
    Rejected
}