using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public interface IPurchaseConfirmationService
{
    Task NotifyAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Regra de confirmação de compra (Card 27/#70). Usa apenas os dados do
/// próprio evento — nunca consulta PaymentsAPI, CatalogAPI ou UsersAPI.
///
/// O evento não traz e-mail do usuário (só UserId). Uma projeção local a
/// partir do UserCreatedEvent (item 5 do escopo) é uma decisão de
/// persistência e fica para o Card 28 — aqui o "destinatário" é só o
/// UserId como identificador, sem e-mail real.
/// </summary>
public sealed class PurchaseConfirmationService(INotificationSender sender) : IPurchaseConfirmationService
{
    public Task NotifyAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken)
    {
        var message = new NotificationMessage(
            Recipient: paymentProcessedEvent.UserId.ToString(),
            Subject: "Compra confirmada",
            Body: $"Compra confirmada para o pedido {paymentProcessedEvent.OrderId}. " +
                  $"Jogo: {paymentProcessedEvent.GameId}, Valor: {paymentProcessedEvent.Amount} {paymentProcessedEvent.Currency}.");

        return sender.SendAsync(message, cancellationToken);
    }
}