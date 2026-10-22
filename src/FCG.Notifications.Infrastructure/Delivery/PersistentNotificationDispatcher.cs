using FCG.Notifications.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.Infrastructure.Delivery;

public sealed partial class PersistentNotificationDispatcher(
    INotificationRepository repository,
    IPersistentSmtpTransport transport,
    NotificationsDispatcherOptions options,
    ILogger<PersistentNotificationDispatcher> logger)
{
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        if (!options.Enabled) return;
        options.Validate();
        await repository.RecoverExpiredAsync(cancellationToken);
        var ids = await repository.GetEligibleIdsAsync(options.BatchSize, cancellationToken);
        foreach (var id in ids)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { await DispatchAsync(id, cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) { LogFailure(logger, id, "PROCESSING", exception.GetType().Name); }
        }
    }

    private async Task DispatchAsync(Guid id, CancellationToken cancellationToken)
    {
        var delivery = await repository.TryAcquireAsync(id, TimeSpan.FromSeconds(options.LeaseSeconds), cancellationToken);
        if (delivery is null) return;
        var token = delivery.ReservationToken!.Value;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        attempt.CancelAfter(TimeSpan.FromSeconds(options.AttemptTimeoutSeconds));
        IPreparedSmtpDelivery session;
        try
        {
            session = await transport.PrepareAsync(delivery.SmtpDestinationId,
                new NotificationMessage(delivery.Destination, delivery.Subject!, delivery.Body!), attempt.Token);
        }
        catch (Exception exception)
        {
            LogFailure(logger, id, "PREPARING", exception.GetType().Name);
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await repository.TryMarkFailedAsync(id, token, "SMTP_PREPARATION_FAILED",
                delivery.AttemptCount < options.MaxAttempts ? DateTimeOffset.UtcNow.AddSeconds(options.RetrySeconds) : null, save.Token);
            return;
        }

        await using (session)
        {
            attempt.Token.ThrowIfCancellationRequested();
            // Autocommit antes de SMTP. Erro/retorno falso nesta fronteira impede qualquer envio.
            if (!await repository.TryStartSendingAsync(id, token, attempt.Token)) return;
            try { await session.SendAsync(attempt.Token); }
            catch (Exception exception)
            {
                LogFailure(logger, id, "SENDING_UNCERTAIN", exception.GetType().Name);
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await repository.TryMarkUnknownAsync(id, token, "SMTP_SEND_UNCERTAIN", save.Token);
                return;
            }

            // SMTP aceitou: repetir somente a gravação, nunca repetir SMTP.
            using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (var retry = 0; retry < 3; retry++)
            {
                try
                {
                    if (await repository.TryMarkSentAsync(id, token, confirmation.Token))
                        LogAccepted(logger, id);
                    else
                        LogFailure(logger, id, "ACCEPTED_NOT_RECORDED", "ReservationUnavailable");
                    return;
                }
                catch (Exception exception)
                {
                    LogFailure(logger, id, "ACCEPTED_NOT_RECORDED", exception.GetType().Name);
                    if (confirmation.IsCancellationRequested) return;
                }
            }
            // Se o banco permanecer indisponível, Sending expirará para Unknown.
        }
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Entrega {NotificationId}: etapa {Stage}, tipo {ExceptionType}. Sem reenvio nesta tentativa.")]
    private static partial void LogFailure(ILogger logger, Guid notificationId, string stage, string exceptionType);
    [LoggerMessage(Level = LogLevel.Information, Message = "Aceitação SMTP persistida para entrega {NotificationId}.")]
    private static partial void LogAccepted(ILogger logger, Guid notificationId);
}
