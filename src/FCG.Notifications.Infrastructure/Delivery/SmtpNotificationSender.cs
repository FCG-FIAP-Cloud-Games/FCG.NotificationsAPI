using FCG.Notifications.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FCG.Notifications.Infrastructure.Delivery;

public sealed partial class SmtpNotificationSender : INotificationSender
{
    private readonly string _host;
    private readonly int _port;
    private readonly string _user;
    private readonly string _pass;
    private readonly string _from;
    private readonly SecureSocketOptions _socketOptions;
    private readonly ILogger<SmtpNotificationSender> _logger;

    public SmtpNotificationSender(IConfiguration configuration, ILogger<SmtpNotificationSender> logger)
    {
        _logger = logger;
        _host = configuration["Smtp:Host"] ?? string.Empty;
        _port = int.TryParse(configuration["Smtp:Port"], out var p) ? p : 587;
        _user = configuration["Smtp:User"] ?? string.Empty;
        _pass = configuration["Smtp:Password"] ?? string.Empty;
        _from = configuration["Smtp:From"] ?? string.Empty;
        var socketOption = configuration["Smtp:SecureSocketOptions"] ?? nameof(SecureSocketOptions.StartTls);
        if (!Enum.TryParse(socketOption, ignoreCase: true, out _socketOptions))
        {
            throw new InvalidOperationException($"Smtp:SecureSocketOptions inválido: '{socketOption}'.");
        }
    }

    public async Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_host) || string.IsNullOrWhiteSpace(_from))
        {
            throw new InvalidOperationException("Configure Smtp:Host e Smtp:From para habilitar o envio de e-mail.");
        }

        if (string.IsNullOrWhiteSpace(_user) != string.IsNullOrWhiteSpace(_pass))
        {
            throw new InvalidOperationException("Configure Smtp:User e Smtp:Password juntos.");
        }

        var email = new MimeMessage();
        email.From.Add(MailboxAddress.Parse(_from));
        email.To.Add(MailboxAddress.Parse(message.Recipient));
        email.Subject = message.Subject;
        email.Body = new TextPart("plain") { Text = message.Body };

        using var smtp = new SmtpClient();
        
        try
        {
            await smtp.ConnectAsync(_host, _port, _socketOptions, cancellationToken);

            if (!string.IsNullOrEmpty(_user))
            {
                await smtp.AuthenticateAsync(_user, _pass, cancellationToken);
            }

            var response = await smtp.SendAsync(email, cancellationToken);
            LogSmtpAccepted(_logger, message.Recipient, _host, _port, response);

            await smtp.DisconnectAsync(true, cancellationToken);
        }
        catch (Exception exception)
        {
            LogSmtpFailed(_logger, exception, message.Recipient, _host, _port);
            throw;
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "SMTP aceitou a mensagem para {Recipient} via {SmtpHost}:{SmtpPort}. Resposta: {SmtpResponse}")]
    private static partial void LogSmtpAccepted(ILogger logger, string recipient, string smtpHost, int smtpPort, string smtpResponse);

    [LoggerMessage(Level = LogLevel.Error, Message = "Falha no envio SMTP para {Recipient} via {SmtpHost}:{SmtpPort}")]
    private static partial void LogSmtpFailed(ILogger logger, Exception exception, string recipient, string smtpHost, int smtpPort);
}
