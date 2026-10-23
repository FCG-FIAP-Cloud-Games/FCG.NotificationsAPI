using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Delivery;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

/// <summary>
/// No modo persistente confirma Inbox e intenções atomicamente.
/// No modo legado mantém a delegação ao serviço de envio direto.
/// </summary>
public sealed partial class PaymentProcessedEventConsumer(
    IPurchaseConfirmationService purchaseConfirmationService,
    ILogger<PaymentProcessedEventConsumer> logger) : IConsumer<PaymentProcessedEvent>
{
    private readonly NotificationsDispatcherOptions _dispatcherOptions = new();
    private readonly IConfiguration? _configuration;
    private readonly IInboxStore? _inbox;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly INotificationRepository? _notifications;

    public PaymentProcessedEventConsumer(IPurchaseConfirmationService purchaseConfirmationService,
        ILogger<PaymentProcessedEventConsumer> logger, NotificationsDispatcherOptions dispatcherOptions,
        IConfiguration configuration, IInboxStore inbox, IUnitOfWork unitOfWork, INotificationRepository notifications)
        : this(purchaseConfirmationService, logger)
    {
        _dispatcherOptions = dispatcherOptions;
        _configuration = configuration;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _notifications = notifications;
    }

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

        if (_dispatcherOptions.Enabled)
        {
            await _unitOfWork!.ExecuteInTransactionAsync(async cancellationToken =>
            {
                if (!await _inbox!.TryReserveAsync(paymentProcessedEvent.EventId, nameof(PaymentProcessedEventConsumer), cancellationToken))
                    return;
                var operationId = paymentProcessedEvent.PaymentId.ToString();
                var existing = await _notifications!.LockBusinessOperationAsync(operationId, NotificationType.PurchaseConfirmation, cancellationToken);
                if (paymentProcessedEvent.Status != PaymentStatus.Approved)
                {
                    if (existing.Count > 0)
                        throw new PermanentMessageException($"Resultado conflitante para PaymentId {paymentProcessedEvent.PaymentId}.");
                    return;
                }
                var message = await purchaseConfirmationService.CreateMessageAsync(paymentProcessedEvent, cancellationToken);
                if (existing.Any(n => n.UserId != paymentProcessedEvent.UserId || n.Subject != message.Subject || n.Body != message.Body))
                    throw new PermanentMessageException($"Dados conflitantes para PaymentId {paymentProcessedEvent.PaymentId}.");
                var sections = _configuration!.GetSection("Smtp:Destinations").GetChildren().ToArray();
                if (sections.Length == 0) sections = [_configuration.GetSection("Smtp")];
                var ids = sections.Select(section => section["Id"]).ToArray();
                if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                    throw new InvalidOperationException("Destinos persistentes exigem Id explícito, único e estável de até 100 caracteres.");
                foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal))
                {
                    if (existing.Any(n => n.SmtpDestinationId == id)) continue;
                    _notifications.Add(new Notification(paymentProcessedEvent.EventId, paymentProcessedEvent.CorrelationId,
                        paymentProcessedEvent.UserId, NotificationType.PurchaseConfirmation, operationId,
                        paymentProcessedEvent.UserId.ToString(), id!, message.Recipient, message.Subject, message.Body, DateTimeOffset.UtcNow));
                }
            }, context.CancellationToken);
            LogPersisted(logger, paymentProcessedEvent.EventId, paymentProcessedEvent.PaymentId, paymentProcessedEvent.Status);
            return;
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

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento de pagamento processado no modo persistente. EventId={EventId}, PaymentId={PaymentId}, Status={Status}")]
    private static partial void LogPersisted(ILogger logger, Guid eventId, Guid paymentId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "PaymentProcessedEvent {EventId} com Status {Status} não gera confirmação (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogPaymentNotApproved(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Processando confirmação de compra para PaymentProcessedEvent {EventId}, Status {Status} (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogProcessingStarted(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId, PaymentStatus status);

    [LoggerMessage(Level = LogLevel.Information, Message = "Confirmação de compra enviada para PaymentProcessedEvent {EventId} (PaymentId: {PaymentId}, OrderId: {OrderId}, CorrelationId: {CorrelationId})")]
    private static partial void LogProcessingCompleted(ILogger logger, Guid eventId, Guid correlationId, Guid paymentId, Guid orderId);
}
