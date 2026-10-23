using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.Application.Notifications;

public interface IWelcomeEmailService
{
    NotificationMessage CreateMessage(UserCreatedEvent userCreatedEvent);
    Task NotifyAsync(UserCreatedEvent userCreatedEvent, CancellationToken cancellationToken);
}

/// <summary>
/// Regra de notificação de boas-vindas (Card 26/#69). Usa apenas os dados do
/// evento — nunca consulta UsersAPI ou UsersDB.
/// </summary>
public sealed class WelcomeEmailService(INotificationSender sender) : IWelcomeEmailService
{
    public NotificationMessage CreateMessage(UserCreatedEvent userCreatedEvent)
    {
        var message = new NotificationMessage
        (
            Recipient: userCreatedEvent.Email,
            Subject: "Bem-vindo à FCG!",
            Body: $"Olá, {userCreatedEvent.Name}! Seu cadastro foi concluído com sucesso. Aproveite nossos serviços!"
        );

        return message;
    }

    public Task NotifyAsync(UserCreatedEvent userCreatedEvent, CancellationToken cancellationToken) =>
        sender.SendAsync(CreateMessage(userCreatedEvent), cancellationToken);
}
