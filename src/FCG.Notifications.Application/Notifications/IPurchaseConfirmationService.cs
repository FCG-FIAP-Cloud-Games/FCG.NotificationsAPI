using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public interface IPurchaseConfirmationService
{
    Task<NotificationMessage> CreateMessageAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken);
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
    public Task<NotificationMessage> CreateMessageAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken) =>
        BuildMessageAsync(paymentProcessedEvent, true, cancellationToken);

    private async Task<NotificationMessage> BuildMessageAsync(PaymentProcessedEvent paymentProcessedEvent, bool persistent, CancellationToken cancellationToken)
    {
        var recipient = await userEmailRepository.GetEmailAsync(paymentProcessedEvent.UserId, cancellationToken);
        if (string.IsNullOrWhiteSpace(recipient))
            throw new InvalidOperationException(
                $"Não há e-mail projetado para o usuário {paymentProcessedEvent.UserId}. " +
                "Confirme que o UserCreatedEvent foi processado pelo Notifications.");

        var message = new NotificationMessage(
            Recipient: recipient,
            Subject: "Compra confirmada",
            Body: persistent
                ? FormattableString.Invariant($"Compra confirmada para o pedido {paymentProcessedEvent.OrderId}. Jogo: {paymentProcessedEvent.GameId}, Valor: {paymentProcessedEvent.Amount:G29} {paymentProcessedEvent.Currency}.")
                : $"Compra confirmada para o pedido {paymentProcessedEvent.OrderId}. " +
                  $"Jogo: {paymentProcessedEvent.GameId}, Valor: {paymentProcessedEvent.Amount} {paymentProcessedEvent.Currency}.");

        return message;
    }

    public async Task NotifyAsync(PaymentProcessedEvent paymentProcessedEvent, CancellationToken cancellationToken) =>
        await sender.SendAsync(await BuildMessageAsync(paymentProcessedEvent, false, cancellationToken), cancellationToken);
}
