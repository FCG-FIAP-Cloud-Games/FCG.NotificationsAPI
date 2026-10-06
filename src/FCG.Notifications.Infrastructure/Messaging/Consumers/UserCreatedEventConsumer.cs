using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Application.Messaging.Contracts;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;



public sealed partial class UserCreatedEventConsumer(
    IWelcomeEmailService welcomeEmailService,
    ILogger<UserCreatedEventConsumer> logger) : IConsumer<UserCreatedEvent>

{
    public async Task Consume(ConsumeContext<UserCreatedEvent> context)
    {
        var userCreatedEvent = context.Message;

        if (!UserCreatedEventValidator.IsValid(userCreatedEvent))
        {
            LogInvalidPayload(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);
            throw new PermanentMessageException($"Evento inválido: {userCreatedEvent}");
        }

        LogProcessingStarted(logger, userCreatedEvent.EventId, userCreatedEvent.CorrelationId);
        
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