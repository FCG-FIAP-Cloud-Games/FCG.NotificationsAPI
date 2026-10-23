using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Delivery;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

public sealed partial class PurchaseNotificationTestEventConsumer(
    INotificationSender sender,
    ILogger<PurchaseNotificationTestEventConsumer> logger) : IConsumer<PurchaseNotificationTestEvent>
{
    private readonly NotificationsDispatcherOptions _dispatcherOptions = new();
    private readonly IConfiguration? _configuration;
    private readonly IInboxStore? _inbox;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly INotificationRepository? _notifications;

    public PurchaseNotificationTestEventConsumer(INotificationSender sender,
        ILogger<PurchaseNotificationTestEventConsumer> logger, NotificationsDispatcherOptions dispatcherOptions,
        IConfiguration configuration, IInboxStore inbox, IUnitOfWork unitOfWork, INotificationRepository notifications)
        : this(sender, logger)
    {
        _dispatcherOptions = dispatcherOptions;
        _configuration = configuration;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _notifications = notifications;
    }

    public async Task Consume(ConsumeContext<PurchaseNotificationTestEvent> context)
    {
        var message = context.Message;
        if (!PurchaseNotificationTestEventValidator.IsValid(message))
            throw new PermanentMessageException($"PurchaseNotificationTestEvent inválido: {message.EventId}.");

        var notificationMessage = new NotificationMessage(
                message.Email,
                "Teste de confirmação de compra FCG",
                $"Teste de envio para o pedido {message.OrderId}. " +
                $"Jogo: {message.GameId}, Valor: {message.Amount} {message.Currency}.");

        if (_dispatcherOptions.Enabled)
        {
            await _unitOfWork!.ExecuteInTransactionAsync(async cancellationToken =>
            {
                if (!await _inbox!.TryReserveAsync(message.EventId, nameof(PurchaseNotificationTestEventConsumer), cancellationToken))
                    return;
                var sections = _configuration!.GetSection("Smtp:Destinations").GetChildren().ToArray();
                if (sections.Length == 0) sections = [_configuration.GetSection("Smtp")];
                var ids = sections.Select(section => section["Id"]).ToArray();
                if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                    throw new InvalidOperationException("Destinos persistentes exigem Id explícito, único e estável de até 100 caracteres.");
                foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal))
                    await _notifications!.TryAddAsync(new Notification(message.EventId, message.CorrelationId,
                        message.UserId, NotificationType.PurchaseConfirmationTest, message.EventId.ToString(),
                        message.UserId.ToString(), id!, notificationMessage.Recipient, notificationMessage.Subject,
                        notificationMessage.Body, DateTimeOffset.UtcNow), cancellationToken);
            }, context.CancellationToken);
            LogTestNotificationPersisted(logger, message.EventId, message.UserId);
            return;
        }

        await sender.SendAsync(notificationMessage, context.CancellationToken);

        LogTestNotificationSent(logger, message.EventId, message.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento de teste de confirmação enviado. EventId={EventId}, UserId={UserId}")]
    private static partial void LogTestNotificationSent(ILogger logger, Guid eventId, Guid userId);

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento de teste processado no modo persistente. EventId={EventId}, UserId={UserId}")]
    private static partial void LogTestNotificationPersisted(ILogger logger, Guid eventId, Guid userId);
}
