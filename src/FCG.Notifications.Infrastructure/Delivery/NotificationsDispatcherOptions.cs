namespace FCG.Notifications.Infrastructure.Delivery;

public sealed class NotificationsDispatcherOptions
{
    public bool Enabled { get; set; }
    public int PollSeconds { get; set; } = 5;
    public int BatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 300;
    public int AttemptTimeoutSeconds { get; set; } = 60;
    public int RetrySeconds { get; set; } = 60;
    public int MaxAttempts { get; set; } = 3;

    public void Validate()
    {
        if (PollSeconds < 1 || BatchSize is < 1 or > 1000 || LeaseSeconds is < 1 or > 3600
            || AttemptTimeoutSeconds < 1 || LeaseSeconds <= AttemptTimeoutSeconds + 30
            || RetrySeconds < 1 || MaxAttempts < 1)
            throw new InvalidOperationException("Configuração inválida de NotificationsDispatcher.");
    }
}
