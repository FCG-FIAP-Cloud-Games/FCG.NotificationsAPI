using System.Net;
using System.Text;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Infrastructure.Delivery;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using MimeKit;

namespace FCG.Notifications.UnitTests;

public class SmtpNotificationSenderTests
{
    private static readonly string[] SuccessfulCalls = ["connect", "authenticate", "send", "disconnect"];

    [Fact]
    public async Task Envio_e_desconexao_bem_sucedidos_confirmam_uma_mensagem()
    {
        using var client = new FakeSmtpClient();
        var logger = new RecordingLogger();

        await CreateSender(client, logger).SendAsync(Message(), TestContext.Current.CancellationToken);

        Assert.Equal(SuccessfulCalls, client.Calls);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level >= LogLevel.Warning);
        AssertSafeLogs(logger);
    }

    [Theory]
    [InlineData("connect", "conexão", 0)]
    [InlineData("authenticate", "autenticação", 0)]
    [InlineData("send", "resultado de entrega pode ser incerto", 1)]
    public async Task Falhas_antes_ou_durante_envio_sao_propagadas(
        string failureStage, string loggedStage, int sends)
    {
        var failure = new IOException("sensitive-server-response");
        using var client = new FakeSmtpClient { FailureStage = failureStage, Failure = failure };
        var logger = new RecordingLogger();

        var actual = await Assert.ThrowsAsync<IOException>(() =>
            CreateSender(client, logger).SendAsync(Message(), TestContext.Current.CancellationToken));

        Assert.Same(failure, actual);
        Assert.Equal(sends, client.Calls.Count(call => call == "send"));
        Assert.DoesNotContain("disconnect", client.Calls);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Information);
        var error = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Error);
        Assert.Contains(loggedStage, error.Text);
        AssertSafeLogs(logger);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Falha_na_desconexao_apos_aceitacao_registra_warning_sem_reenviar(bool cancellation)
    {
        using var cancellationSource = new CancellationTokenSource();
        using var client = new FakeSmtpClient
        {
            FailureStage = "disconnect",
            Failure = cancellation ? new OperationCanceledException() : new IOException("sensitive-server-response"),
            OnDisconnect = cancellation ? cancellationSource.Cancel : null
        };
        var logger = new RecordingLogger();

        await CreateSender(client, logger).SendAsync(Message(), cancellationSource.Token);

        Assert.Equal(SuccessfulCalls, client.Calls);
        Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Information);
        var warning = Assert.Single(logger.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Contains("após aceitação SMTP", warning.Text);
        Assert.DoesNotContain(logger.Entries, entry => entry.Level == LogLevel.Error);
        AssertSafeLogs(logger);
    }

    private static SmtpNotificationSender CreateSender(FakeSmtpClient client, RecordingLogger logger)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Host"] = "smtp.example.test",
            ["Smtp:Port"] = "587",
            ["Smtp:SecureSocketOptions"] = "StartTls",
            ["Smtp:From"] = "sender@example.test",
            ["Smtp:User"] = "private-user",
            ["Smtp:Password"] = "private-password"
        }).Build();
        return new SmtpNotificationSender(configuration, logger, () => client);
    }

    private static NotificationMessage Message() => new("recipient@example.test", "private-subject", "private-body");

    private static void AssertSafeLogs(RecordingLogger logger)
    {
        foreach (var entry in logger.Entries)
        {
            Assert.Null(entry.Exception);
            foreach (var sensitive in new[] { "recipient@example.test", "sender@example.test", "private-user", "private-password", "private-subject", "private-body", "sensitive-server-response" })
                Assert.DoesNotContain(sensitive, entry.Text);
        }
    }

    private sealed class FakeSmtpClient : SmtpClient
    {
        public List<string> Calls { get; } = [];
        public string? FailureStage { get; init; }
        public Exception Failure { get; init; } = new IOException();
        public Action? OnDisconnect { get; init; }

        private Task Step(string stage)
        {
            Calls.Add(stage);
            return stage == FailureStage ? Task.FromException(Failure) : Task.CompletedTask;
        }

        public override Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
            => Step("connect");

        public override Task AuthenticateAsync(Encoding encoding, ICredentials credentials, CancellationToken cancellationToken = default)
            => Step("authenticate");

        public override async Task<string> SendAsync(FormatOptions options, MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null)
        {
            await Step("send");
            return "sensitive-server-response";
        }

        public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            OnDisconnect?.Invoke();
            return Step("disconnect");
        }
    }

    private sealed class RecordingLogger : ILogger<SmtpNotificationSender>
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
