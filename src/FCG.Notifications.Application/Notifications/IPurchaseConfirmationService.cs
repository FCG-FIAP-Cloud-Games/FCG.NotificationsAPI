using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public interface IPurchaseConfirmationService
{
    Task NotifyAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Resolves the recipient from Notifications' local projection populated by
/// Users events. The Payments event remains free of user contact data.
/// </summary>
public sealed class PurchaseConfirmationService(
    INotificationSender sender,
    IUserEmailRepository userEmailRepository) : IPurchaseConfirmationService
{
    public async Task NotifyAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken)
    {
        var recipient = await userEmailRepository.GetEmailAsync(paymentProcessedEvent.UserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException(
                $"Não há e-mail projetado para o usuário {paymentProcessedEvent.UserId}. " +
                "Confirme que o UserCreatedEvent foi processado pelo Notifications.");

        var message = new NotificationMessage(
            Recipient: recipient,
            Subject: "Compra confirmada",
            Body: $"Compra confirmada para o pedido {paymentProcessedEvent.OrderId}. " +
                  $"Jogo: {paymentProcessedEvent.GameId}, Valor: {paymentProcessedEvent.Amount} {paymentProcessedEvent.Currency}.");

        await sender.SendAsync(message, cancellationToken);
    }
}
