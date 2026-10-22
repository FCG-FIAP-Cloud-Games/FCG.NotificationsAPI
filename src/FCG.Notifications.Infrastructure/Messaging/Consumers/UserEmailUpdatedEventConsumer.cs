using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

public sealed partial class UserEmailUpdatedEventConsumer(
    IUserEmailRepository userEmailRepository,
    ILogger<UserEmailUpdatedEventConsumer> logger) : IConsumer<UserEmailUpdatedEvent>
{
    public async Task Consume(ConsumeContext<UserEmailUpdatedEvent> context)
    {
        var message = context.Message;
        if (!UserEmailUpdatedEventValidator.IsValid(message))
            throw new PermanentMessageException($"UserEmailUpdatedEvent inválido: {message.EventId}.");

        await userEmailRepository.UpsertAsync(
            message.UserId,
            message.Email,
            message.OccurredAt,
            context.CancellationToken);

        LogEmailUpdated(logger, message.EventId, message.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail atualizado na projeção de Notifications. EventId={EventId}, UserId={UserId}")]
    private static partial void LogEmailUpdated(ILogger logger, Guid eventId, Guid userId);
}
