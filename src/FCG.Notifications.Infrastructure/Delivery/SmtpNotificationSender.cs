using FCG.Notifications.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FCG.Notifications.Infrastructure.Delivery;

public sealed partial class SmtpNotificationSender : INotificationSender
{
    private readonly IReadOnlyList<SmtpDestination> _destinations;
    private readonly ILogger<SmtpNotificationSender> _logger;

    public SmtpNotificationSender(IConfiguration configuration, ILogger<SmtpNotificationSender> logger)
    {
        _logger = logger;
        var configuredDestinations = configuration.GetSection("Smtp:Destinations").GetChildren()
            .Select(section => CreateDestination(section.Key, section))
            .ToArray();

        // Compatibilidade com configurações antigas de um único servidor SMTP.
        _destinations = configuredDestinations.Length > 0
            ? configuredDestinations
            : [CreateDestination("SMTP", configuration.GetSection("Smtp"))];

        foreach (var destination in _destinations)
        {
            if (string.IsNullOrWhiteSpace(destination.Host) || string.IsNullOrWhiteSpace(destination.From))
                throw new InvalidOperationException(
                    $"Configure Smtp:Destinations:{destination.Name}:Host e From para habilitar o envio.");
            if (string.IsNullOrWhiteSpace(destination.User) != string.IsNullOrWhiteSpace(destination.Password))
                throw new InvalidOperationException(
                    $"Configure User e Password juntos para o destino SMTP '{destination.Name}'.");
        }
    }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        var failures = new List<Exception>();
        foreach (var destination in _destinations)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await SendToDestinationAsync(destination, message, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                LogSmtpFailed(_logger, exception, message.Recipient, destination.Name, destination.Host, destination.Port);
                failures.Add(exception);
            }
        }

        if (failures.Count == 1)
            throw failures[0];
        if (failures.Count > 1)
            throw new AggregateException("Falha no envio para um ou mais destinos SMTP.", failures);
    }

    private static SmtpDestination CreateDestination(string name, IConfiguration configuration)
    {
        var socketOption = configuration["SecureSocketOptions"] ?? nameof(SecureSocketOptions.StartTls);
        if (!Enum.TryParse(socketOption, ignoreCase: true, out SecureSocketOptions parsedSocketOption))
            throw new InvalidOperationException($"Smtp:SecureSocketOptions inválido no destino '{name}': '{socketOption}'.");

        return new SmtpDestination(
            name,
            configuration["Host"] ?? string.Empty,
            int.TryParse(configuration["Port"], out var port) ? port : 587,
            configuration["User"] ?? string.Empty,
            configuration["Password"] ?? string.Empty,
            configuration["From"] ?? string.Empty,
            parsedSocketOption);
    }

    private async Task SendToDestinationAsync(
        SmtpDestination destination,
        NotificationMessage message,
        CancellationToken cancellationToken)
    {
        var email = new MimeMessage();
        email.From.Add(MailboxAddress.Parse(destination.From));
        email.To.Add(MailboxAddress.Parse(message.Recipient));
        email.Subject = message.Subject;
        email.Body = new TextPart("plain") { Text = message.Body };

        using var smtp = new SmtpClient();
        await smtp.ConnectAsync(destination.Host, destination.Port, destination.SocketOptions, cancellationToken);
        if (!string.IsNullOrEmpty(destination.User))
            await smtp.AuthenticateAsync(destination.User, destination.Password, cancellationToken);

        var response = await smtp.SendAsync(email, cancellationToken);
        LogSmtpAccepted(_logger, message.Recipient, destination.Name, destination.Host, destination.Port, response);
        await smtp.DisconnectAsync(true, cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP aceitou a mensagem para {Recipient} no destino {Destination} ({SmtpHost}:{SmtpPort}). Resposta: {SmtpResponse}")]
    private static partial void LogSmtpAccepted(ILogger logger, string recipient, string destination, string smtpHost, int smtpPort, string smtpResponse);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no envio SMTP para {Recipient} no destino {Destination} ({SmtpHost}:{SmtpPort})")]
    private static partial void LogSmtpFailed(ILogger logger, Exception exception, string recipient, string destination, string smtpHost, int smtpPort);

    private sealed record SmtpDestination(
        string Name,
        string Host,
        int Port,
        string User,
        string Password,
        string From,
        SecureSocketOptions SocketOptions);
}
