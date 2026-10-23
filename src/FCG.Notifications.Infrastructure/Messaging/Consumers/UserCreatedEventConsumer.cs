using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Application.Messaging.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using FCG.Notifications.Infrastructure.Delivery;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;



public sealed partial class UserCreatedEventConsumer(
    IWelcomeEmailService welcomeEmailService,
    IUserEmailRepository userEmailRepository,
    ILogger<UserCreatedEventConsumer> logger) : IConsumer<UserCreatedEvent>

{
    private readonly NotificationsDispatcherOptions _dispatcherOptions = new();
    private readonly IConfiguration? _configuration;
    private readonly IInboxStore? _inbox;
    private readonly IUnitOfWork? _unitOfWork;
    private readonly INotificationRepository? _notifications;

    // Mantém o construtor legado para callers existentes. DI usa o construtor completo.
    public UserCreatedEventConsumer(IWelcomeEmailService welcomeEmailService, IUserEmailRepository userEmailRepository,
        ILogger<UserCreatedEventConsumer> logger, NotificationsDispatcherOptions dispatcherOptions,
        IConfiguration configuration, IInboxStore inbox, IUnitOfWork unitOfWork, INotificationRepository notifications)
        : this(welcomeEmailService, userEmailRepository, logger)
    {
        _dispatcherOptions = dispatcherOptions;
        _configuration = configuration;
        _inbox = inbox;
        _unitOfWork = unitOfWork;
        _notifications = notifications;
    }

    public async Task Consume(ConsumeContext<UserCreatedEvent> context)
    {
        var userCreatedEvent = context.Message;

        if (!UserCreatedEventValidator.IsValid(userCreatedEvent))
        {
            LogInvalidPayload(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);
            throw new PermanentMessageException($"UserCreatedEvent inválido: {userCreatedEvent.EventId}");
        }

        LogProcessingStarted(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);

        if (_dispatcherOptions.Enabled)
        {
            await _unitOfWork!.ExecuteInTransactionAsync(async cancellationToken =>
            {
                if (!await _inbox!.TryReserveAsync(userCreatedEvent.EventId, nameof(UserCreatedEventConsumer), cancellationToken))
                    return;
                await userEmailRepository.UpsertAsync(userCreatedEvent.UserId, userCreatedEvent.Email,
                    userCreatedEvent.OccurredAt, cancellationToken);
                var sections = _configuration!.GetSection("Smtp:Destinations").GetChildren().ToArray();
                if (sections.Length == 0) sections = [_configuration.GetSection("Smtp")];
                var ids = sections.Select(section => section["Id"]).ToArray();
                if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                    throw new InvalidOperationException("Destinos persistentes exigem Id explícito, único e estável de até 100 caracteres.");
                var message = welcomeEmailService.CreateMessage(userCreatedEvent);
                foreach (var id in ids.OrderBy(id => id, StringComparer.Ordinal))
                {
                    var notification = new Notification(userCreatedEvent.EventId, userCreatedEvent.CorrelationId,
                        userCreatedEvent.UserId, NotificationType.Welcome, userCreatedEvent.UserId.ToString(),
                        userCreatedEvent.UserId.ToString(), id!, message.Recipient, message.Subject, message.Body, DateTimeOffset.UtcNow);
                    await _notifications!.TryAddAsync(notification, cancellationToken);
                }
            }, context.CancellationToken);
            LogProcessingCompleted(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);
            return;
        }

        await userEmailRepository.UpsertAsync(
            userCreatedEvent.UserId,
            userCreatedEvent.Email,
            userCreatedEvent.OccurredAt,
            context.CancellationToken);

        await welcomeEmailService.NotifyAsync(userCreatedEvent, context.CancellationToken);

        LogProcessingCompleted(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Payload inválido para UserCreatedEvent | EventId: {EventId} | CorrelationId: {CorrelationId}")]
    private static partial void LogInvalidPayload
        (
            ILogger logger,
            Guid eventId,
            Guid correlationId
        );

     [LoggerMessage(Level = LogLevel.Information, Message = "Processando UserCreatedEvent | EventId: {EventId} | CorrelationId: {CorrelationId}")]   
    private static partial void LogProcessingStarted
        (
            ILogger logger,
            Guid eventId,
            Guid correlationId
        );
    
    [LoggerMessage(Level = LogLevel.Information, Message = "UserCreatedEvent processado | EventId: {EventId} | CorrelationId: {CorrelationId}")]   
    private static partial void LogProcessingCompleted
        (
            ILogger logger,
            Guid eventId,
            Guid correlationId
        );
}
