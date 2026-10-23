using FCG.Notifications.Application.Abstractions;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FCG.Notifications.Infrastructure.Delivery;

public sealed partial class PersistentSmtpTransport : IPersistentSmtpTransport
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PersistentSmtpTransport> _logger;
    private readonly Func<ISmtpClient> _createClient;

    public PersistentSmtpTransport(IConfiguration configuration, ILogger<PersistentSmtpTransport> logger)
        : this(configuration, logger, () => new SmtpClient()) { }

    public PersistentSmtpTransport(IConfiguration configuration, ILogger<PersistentSmtpTransport> logger, Func<ISmtpClient> createClient)
    {
        _configuration = configuration;
        _logger = logger;
        _createClient = createClient;
    }

    public async Task<IPreparedSmtpDelivery> PrepareAsync(string destinationId, NotificationMessage message, string messageId, CancellationToken cancellationToken)
    {
        // IDs explícitos: nunca usar posição de array, host ou fallback para redirecionar uma entrega.
        var destinations = _configuration.GetSection("Smtp:Destinations").GetChildren().ToArray();
        if (destinations.Length == 0) destinations = [_configuration.GetSection("Smtp")];
        if (destinations.Any(d => string.IsNullOrWhiteSpace(d["Id"]))
            || destinations.Select(d => d["Id"]).Distinct(StringComparer.Ordinal).Count() != destinations.Length)
            throw new InvalidOperationException("Destinos persistentes exigem Id explícito, único e estável.");
        var destination = destinations.SingleOrDefault(d => string.Equals(d["Id"], destinationId, StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Destino SMTP persistido não está configurado.");
        var host = destination["Host"];
        var from = destination["From"];
        var user = destination["User"];
        var password = destination["Password"];
        if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(from)
            || !int.TryParse(destination["Port"], out var port) || port is < 1 or > 65535
            || string.IsNullOrWhiteSpace(user) != string.IsNullOrWhiteSpace(password)
            || !Enum.TryParse<SecureSocketOptions>(destination["SecureSocketOptions"] ?? "StartTls", true, out var security)
            || !Enum.IsDefined(security))
            throw new InvalidOperationException("Configuração SMTP inválida para entrega persistente.");
        var email = new MimeMessage { MessageId = messageId };
        email.From.Add(MailboxAddress.Parse(from));
        email.To.Add(MailboxAddress.Parse(message.Recipient));
        email.Subject = message.Subject;
        email.Body = new TextPart("plain") { Text = message.Body };
        var session = new Session(_createClient(), email, _logger);
        try
        {
            await session.Client.ConnectAsync(host, port, security, cancellationToken);
            if (!string.IsNullOrWhiteSpace(user))
                await session.Client.AuthenticateAsync(user, password!, cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private sealed class Session(ISmtpClient client, MimeMessage email, ILogger logger) : IPreparedSmtpDelivery
    {
        public ISmtpClient Client { get; } = client;
        public async Task<SmtpAcceptanceEvidence> SendAsync(CancellationToken cancellationToken) =>
            SmtpAcceptanceEvidence.FromSuccessfulResponse(await Client.SendAsync(email, cancellationToken));
        public async ValueTask DisposeAsync()
        {
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                if (Client.IsConnected) await Client.DisconnectAsync(true, timeout.Token);
            }
            catch (Exception exception) { LogCleanupFailed(logger, exception.GetType().Name); }
            finally
            {
                try { Client.Dispose(); }
                catch (Exception exception) { LogCleanupFailed(logger, exception.GetType().Name); }
                email.Dispose();
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao encerrar sessão SMTP; não altera resultado do envio. Tipo={ExceptionType}")]
    private static partial void LogCleanupFailed(ILogger logger, string exceptionType);
}
