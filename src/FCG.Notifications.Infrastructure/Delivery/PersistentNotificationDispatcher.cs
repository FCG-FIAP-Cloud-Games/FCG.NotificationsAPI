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
        var recovered = await repository.RecoverExpiredAsync(cancellationToken);
        if (recovered > 0) LogRecovered(logger, recovered);
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
        LogProgress(logger, id, "Preparing", "ACQUIRED", delivery.SmtpDestinationId, delivery.AttemptCount);
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
            LogFailure(logger, id, "PREPARING", exception.GetType().Name, "Preparing", delivery.SmtpDestinationId, delivery.AttemptCount);
            using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var recorded = await repository.TryMarkFailedAsync(id, token, "SMTP_PREPARATION_FAILED",
                delivery.AttemptCount < options.MaxAttempts ? DateTimeOffset.UtcNow.AddSeconds(options.RetrySeconds) : null, save.Token);
            LogProgress(logger, id, recorded ? "Failed" : "Unconfirmed", recorded ? "FAILURE_RECORDED" : "FAILURE_NOT_RECORDED",
                delivery.SmtpDestinationId, delivery.AttemptCount);
            return;
        }

        await using (session)
        {
            attempt.Token.ThrowIfCancellationRequested();
            // Autocommit antes de SMTP. Erro/retorno falso nesta fronteira impede qualquer envio.
            if (!await repository.TryStartSendingAsync(id, token, attempt.Token))
            {
                LogProgress(logger, id, "Unconfirmed", "RESERVATION_UNAVAILABLE", delivery.SmtpDestinationId, delivery.AttemptCount);
                return;
            }
            LogProgress(logger, id, "Sending", "SMTP_SEND_START", delivery.SmtpDestinationId, delivery.AttemptCount);
            try { await session.SendAsync(attempt.Token); }
            catch (Exception exception)
            {
                LogFailure(logger, id, "SENDING_UNCERTAIN", exception.GetType().Name, "Sending", delivery.SmtpDestinationId, delivery.AttemptCount);
                using var save = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                var recorded = await repository.TryMarkUnknownAsync(id, token, "SMTP_SEND_UNCERTAIN", save.Token);
                LogProgress(logger, id, recorded ? "Unknown" : "Unconfirmed", recorded ? "UNKNOWN_RECORDED" : "UNKNOWN_NOT_RECORDED",
                    delivery.SmtpDestinationId, delivery.AttemptCount);
                return;
            }

            // SMTP aceitou: repetir somente a gravação, nunca repetir SMTP.
            using var confirmation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            for (var retry = 0; retry < 3; retry++)
            {
                try
                {
                    if (await repository.TryMarkSentAsync(id, token, confirmation.Token))
                        LogProgress(logger, id, "Sent", "ACCEPTED_RECORDED", delivery.SmtpDestinationId, delivery.AttemptCount);
                    else
                        LogFailure(logger, id, "ACCEPTED_NOT_RECORDED", "ReservationUnavailable", "Unconfirmed", delivery.SmtpDestinationId, delivery.AttemptCount);
                    return;
                }
                catch (Exception exception)
                {
                    LogFailure(logger, id, "ACCEPTED_NOT_RECORDED", exception.GetType().Name, "Unconfirmed", delivery.SmtpDestinationId, delivery.AttemptCount);
                    if (confirmation.IsCancellationRequested) return;
                }
            }
            // Se o banco permanecer indisponível, Sending expirará para Unknown.
        }
    }

    // Unconfirmed/null is diagnostic uncertainty, never a new persisted notification state.
    [LoggerMessage(Level = LogLevel.Error, Message = "Entrega {NotificationId}: estado {Status}, etapa {Stage}, destino {SmtpDestinationId}, aquisição {AttemptCount}, tipo {ExceptionType}. Sem reenvio nesta tentativa.")]
    private static partial void LogFailure(ILogger logger, Guid notificationId, string stage, string exceptionType,
        string? status = null, string? smtpDestinationId = null, int? attemptCount = null);
    [LoggerMessage(Level = LogLevel.Information, Message = "Entrega {NotificationId}: estado {Status}, etapa {Stage}, destino {SmtpDestinationId}, aquisição {AttemptCount}.")]
    private static partial void LogProgress(ILogger logger, Guid notificationId, string status, string stage, string smtpDestinationId, int attemptCount);
    [LoggerMessage(Level = LogLevel.Information, Message = "Recuperação de reservas expiradas: {RecoveredCount} entregas atualizadas.")]
    private static partial void LogRecovered(ILogger logger, int recoveredCount);
}
