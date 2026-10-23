using FCG.Notifications.Application.Abstractions;

namespace FCG.Notifications.UnitTests.TestDoubles;

/// <summary>
/// Dublê de INotificationSender que só grava a última mensagem enviada,
/// para os testes verificarem o que o WelcomeEmailService/consumer produziu.
/// </summary>
public sealed class RecordingNotificationSender : INotificationSender
{
    public NotificationMessage? LastMessage { get; private set; }

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        LastMessage = message;
        return Task.CompletedTask;
    }
}