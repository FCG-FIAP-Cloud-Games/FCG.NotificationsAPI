using FCG.Notifications.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Delivery;

/// <summary>
/// Implementação de envio simulado via log (Card 25 previu a abstração;
/// Card 26 entrega a primeira implementação concreta). Trocar por um
/// provedor real no futuro não exige alterar nenhum consumer.
/// </summary>
public sealed partial class ConsoleNotificationSender(ILogger<ConsoleNotificationSender> logger) : INotificationSender
{
    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        LogNotificationSent(logger, message.Recipient, message.Subject);

        return Task.CompletedTask;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "E-mail de boas-vindas enviado para {Email} | Assunto: {Subject}")]
    private static partial void LogNotificationSent
        (
            ILogger logger, 
            string email, 
            string subject
        );
}