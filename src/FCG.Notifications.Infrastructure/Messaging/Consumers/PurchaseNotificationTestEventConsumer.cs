using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

public sealed partial class PurchaseNotificationTestEventConsumer(
    INotificationSender sender,
    ILogger<PurchaseNotificationTestEventConsumer> logger) : IConsumer<PurchaseNotificationTestEvent>
{
    public async Task Consume(ConsumeContext<PurchaseNotificationTestEvent> context)
    {
        var message = context.Message;
        if (!PurchaseNotificationTestEventValidator.IsValid(message))
            throw new PermanentMessageException($"PurchaseNotificationTestEvent inválido: {message.EventId}.");

        await sender.SendAsync(
            new NotificationMessage(
                message.Email,
                "Teste de confirmação de compra FCG",
                $"Teste de envio para o pedido {message.OrderId}. " +
                $"Jogo: {message.GameId}, Valor: {message.Amount} {message.Currency}."),
            context.CancellationToken);

        LogTestNotificationSent(logger, message.EventId, message.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento de teste de confirmação enviado. EventId={EventId}, UserId={UserId}")]
    private static partial void LogTestNotificationSent(ILogger logger, Guid eventId, Guid userId);
}
