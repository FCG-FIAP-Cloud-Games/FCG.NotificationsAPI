using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Messaging.Consumers;

public sealed partial class UserEmailUpdatedEventConsumer(
    IUserEmailRepository userEmailRepository,
    IInboxStore inboxStore,
    IUnitOfWork unitOfWork,
    ILogger<UserEmailUpdatedEventConsumer> logger) : IConsumer<UserEmailUpdatedEvent>
{
    public async Task Consume(ConsumeContext<UserEmailUpdatedEvent> context)
    {
        var message = context.Message;
        if (!UserEmailUpdatedEventValidator.IsValid(message))
            throw new PermanentMessageException($"UserEmailUpdatedEvent inválido: {message.EventId}.");

        await unitOfWork.ExecuteInTransactionAsync(async cancellationToken =>
        {
            if (!await inboxStore.TryReserveAsync(message.EventId, nameof(UserEmailUpdatedEventConsumer), cancellationToken))
                return;

            await userEmailRepository.UpsertAsync(
                message.UserId,
                message.Email,
                message.OccurredAt,
                cancellationToken);
        }, context.CancellationToken);

        LogEmailUpdated(logger, message.EventId, message.UserId);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Evento de atualização de e-mail processado. EventId={EventId}, UserId={UserId}")]
    private static partial void LogEmailUpdated(ILogger logger, Guid eventId, Guid userId);
}
