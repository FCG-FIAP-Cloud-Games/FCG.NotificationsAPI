using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

/// <summary>
/// Valida o contrato mínimo exigido pelo Card 26 (#69) antes de processar
/// o UserCreatedEvent. Payload inválido não segue para o envio normal.
/// </summary>
public static class UserCreatedEventValidator
{
    public static bool IsValid(UserCreatedEvent @event) =>
        @event.EventId != Guid.Empty &&
        @event.CorrelationId != Guid.Empty &&
        @event.UserId != Guid.Empty &&
        !string.IsNullOrWhiteSpace(@event.Name) &&
        !string.IsNullOrWhiteSpace(@event.Email);
}