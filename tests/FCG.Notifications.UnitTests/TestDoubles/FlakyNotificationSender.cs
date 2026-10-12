using FCG.Notifications.Application.Abstractions;

namespace FCG.Notifications.UnitTests.TestDoubles;

/// <summary>
/// Dublê que falha na primeira tentativa (simula falha transitória) e
/// sucede na segunda, para testar a reprocessagem com retry do consumer.
/// </summary>
public sealed class FlakyNotificationSender : INotificationSender
{
    private int _attempts;

    public int Attempts => _attempts;
    public NotificationMessage? LastMessage { get; private set; }

    public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        if (_attempts == 1)
        {
            throw new InvalidOperationException("Falha transitória simulada.");
        }

        LastMessage = message;
        return Task.CompletedTask;
    }
}