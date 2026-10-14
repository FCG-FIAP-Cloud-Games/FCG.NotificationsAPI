using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

/// <summary>
/// Recebimento do PaymentProcessedEvent (Card 27/#70). Só valida, decide
/// Approved/Rejected, loga e delega — a regra de confirmação vive em
/// PurchaseConfirmationService. Mesma separação do C26, para o Card 28
/// adicionar Inbox aqui sem tocar na regra nem no envio.
/// </summary>
public sealed partial class PaymentProcessedEventConsumer(
    IPurchaseConfirmationService purchaseConfirmationService,
    ILogger<PaymentProcessedEventConsumer> logger) : IConsumer<PaymentProcessedEvent>
{
    public async Task Consume(ConsumeContext<PaymentProcessedEvent> context)
    {
        var paymentProcessedEvent = context.Message;

        if (!PaymentProcessedEventValidator.IsValid(paymentProcessedEvent))
        {
            LogInvalidPayload(
                logger,
                paymentProcessedEvent.EventId,
                paymentProcessedEvent.CorrelationId,
                paymentProcessedEvent.PaymentId,
                paymentProcessedEvent.OrderId);

            // Falha permanente: vai direto para a fila de erro
            // (notifications-payment-processed_error), sem retry.
            throw new PermanentMessageException(
                $"PaymentProcessedEvent {paymentProcessedEvent.EventId} com payload inválido.");
        }

        if (paymentProcessedEvent.Status != PaymentStatus.Approved)
        {
            LogPaymentNotApproved(
                logger,
                paymentProcessedEvent.EventId,
                paymentProcessedEvent.CorrelationId,
                paymentProcessedEvent.PaymentId,
                paymentProcessedEvent.OrderId,
                paymentProcessedEvent.Status);

            // ACK implícito: Rejected é um resultado válido, só não gera
            // confirmação de compra (item 3 do escopo).
            return;
        }

        LogProcessingStarted(
            logger,
            paymentProcessedEvent.EventId,
            paymentProcessedEvent.CorrelationId,
            paymentProcessedEvent.PaymentId,
            paymentProcessedEvent.OrderId,
            paymentProcessedEvent.Status);

        await purchaseConfirmationService.NotifyAsync(paymentProcessedEvent, context.CancellationToken);

        LogProcessingCompleted(
            logger,
            paymentProcessedEvent.EventId,
            paymentProcessedEvent.CorrelationId,
            paymentProcessedEvent.PaymentId,
            paymentProcessedEvent.OrderId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payload inválido para PaymentProcessedEvent {EventId} (CorrelationId: {CorrelationId}, PaymentId: {PaymentId}, OrderId: {OrderId})")]
    private static partial void LogInvalidPayload(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId);

    [LoggerMessage(Level = LogLevel.Information, Message = "PaymentProcessedEvent {EventId} com Status {Status} não gera confirmação (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogPaymentNotApproved(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processando confirmação de compra para PaymentProcessedEvent {EventId}, Status {Status} (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogProcessingStarted(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirmação de compra enviada para PaymentProcessedEvent {EventId} (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogProcessingCompleted(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId);
}