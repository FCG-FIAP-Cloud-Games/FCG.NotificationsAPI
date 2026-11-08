using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Delivery;

public sealed partial class NotificationsDispatcherWorker(
    IServiceScopeFactory scopeFactory,
    NotificationsDispatcherOptions options,
    ILogger<NotificationsDispatcherWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Enabled) return;
        options.Validate();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<PersistentNotificationDispatcher>().RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception exception) { LogCycleFailed(logger, exception.GetType().Name); }
            try { await Task.Delay(TimeSpan.FromSeconds(options.PollSeconds), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Ciclo do dispatcher interrompido. Tipo={ExceptionType}")]
    private static partial void LogCycleFailed(ILogger logger, string exceptionType);
}
