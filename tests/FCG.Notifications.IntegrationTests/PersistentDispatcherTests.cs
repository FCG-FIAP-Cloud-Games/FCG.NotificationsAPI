using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using FCG.Notifications.Infrastructure.Delivery;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging;
using MimeKit;
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class PersistentDispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("connect", "Failed", "FAILURE_RECORDED")]
    [InlineData("send", "Unknown", "UNKNOWN_RECORDED")]
    [InlineData("none", "Sent", "ACCEPTED_RECORDED")]
    public async Task Logs_estruturados_correlacionam_entrega_sem_conteudo_sensivel(string failure, string status, string stage)
    {
        await using var database = await TestDatabase.CreateAsync();
        var delivery = Delivery();
        await database.InsertAsync(delivery);
        await using var db = database.Open();
        var logger = new RecordingDispatcherLogger();
        var transport = Transport(() => new FakeSmtpClient
        {
            OnConnect = () => failure == "connect" ? Task.FromException(new IOException("secret-exception")) : Task.CompletedTask,
            OnSend = () => failure == "send" ? Task.FromException(new IOException("secret-exception")) : Task.CompletedTask
        });
        await new PersistentNotificationDispatcher(new NotificationRepository(db), transport,
            new NotificationsDispatcherOptions { Enabled = true }, logger).RunOnceAsync(Ct);
        Assert.Contains(logger.Entries, entry => Equals(entry["Stage"], stage) && Equals(entry["Status"], status));
        Assert.All(logger.Entries, entry =>
        {
            Assert.Equal(delivery.Id, entry["NotificationId"]);
            Assert.Equal(delivery.SmtpDestinationId, entry["SmtpDestinationId"]);
            Assert.Equal(1, entry["AttemptCount"]);
            Assert.DoesNotContain(delivery.Destination, string.Join(" ", entry.Values), StringComparison.Ordinal);
            Assert.DoesNotContain("secret-exception", string.Join(" ", entry.Values), StringComparison.Ordinal);
            Assert.DoesNotContain("Body", entry.Keys);
            Assert.DoesNotContain("Subject", entry.Keys);
            Assert.DoesNotContain("ReservationToken", entry.Keys);
        });
    }

    private sealed class RecordingDispatcherLogger : ILogger<PersistentNotificationDispatcher>
    {
        public List<Dictionary<string, object?>> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Assert.Null(exception);
            Entries.Add(((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(pair => pair.Key, pair => pair.Value));
        }
    }

    [Fact]
    public async Task Pending_sobrevive_nova_instancia_e_Sending_e_persistido_antes_do_SMTP()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(Delivery());
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient
        {
            OnSend = async () =>
            {
                await using var verify = database.Open();
                Assert.Equal(NotificationStatus.Sending, (await verify.Notifications.SingleAsync(Ct)).Status);
                Interlocked.Increment(ref sends);
            }
        });
        await RunAsync(database, transport);
        await RunAsync(database, transport);
        await using var db = database.Open();
        var result = await db.Notifications.SingleAsync(n => n.Id == id, Ct);
        Assert.Equal(NotificationStatus.Sent, result.Status);
        Assert.NotNull(result.SentAt);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData("connect", NotificationStatus.Failed, 0)]
    [InlineData("send", NotificationStatus.Unknown, 1)]
    [InlineData("disconnect", NotificationStatus.Sent, 1)]
    public async Task Falhas_respeitam_fronteira_de_aceitacao(string failure, NotificationStatus status, int expectedSends)
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InsertAsync(Delivery());
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient { Failure = failure, OnSend = () => { sends++; return Task.CompletedTask; } });
        await RunAsync(database, transport);
        await RunAsync(database, transport);
        await using var db = database.Open();
        Assert.Equal(status, (await db.Notifications.SingleAsync(Ct)).Status);
        Assert.Equal(expectedSends, sends);
    }

    [Fact]
    public async Task Dois_dispatchers_so_enviam_uma_vez()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InsertAsync(Delivery());
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient { OnSend = async () => { Interlocked.Increment(ref sends); await Task.Delay(100, Ct); } });
        await Task.WhenAll(RunAsync(database, transport), RunAsync(database, transport));
        Assert.Equal(1, sends);
        await using var db = database.Open();
        Assert.Equal(NotificationStatus.Sent, (await db.Notifications.SingleAsync(Ct)).Status);
    }

    [Theory]
    [InlineData(false, NotificationStatus.Sent, 1)]
    [InlineData(true, NotificationStatus.Unknown, 0)]
    public async Task Recuperacao_distingue_Preparing_de_Sending(bool sending, NotificationStatus status, int expectedSends)
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(Delivery());
        await using (var db = database.Open())
        {
            var repository = new NotificationRepository(db);
            var acquired = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
            if (sending) Assert.True(await repository.TryStartSendingAsync(id, acquired.ReservationToken!.Value, Ct));
            await ExpireAsync(db, id);
        }
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient { OnSend = () => { sends++; return Task.CompletedTask; } });
        await RunAsync(database, transport);
        await RunAsync(database, transport);
        await using var verify = database.Open();
        Assert.Equal(status, (await verify.Notifications.SingleAsync(Ct)).Status);
        Assert.Equal(expectedSends, sends);
    }

    [Fact]
    public async Task Reserva_expirada_durante_preparacao_impede_SendAsync()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(Delivery());
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient
        {
            OnConnect = async () => { await using var db = database.Open(); await ExpireAsync(db, id); },
            OnSend = () => { sends++; return Task.CompletedTask; }
        });
        await RunAsync(database, transport);
        Assert.Equal(0, sends);
    }

    [Fact]
    public async Task Falha_de_um_destino_nao_repete_outro_e_Id_nao_depende_da_ordem()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InsertAsync(Delivery("gmail"));
        await database.InsertAsync(Delivery("mailhog"));
        var hosts = new List<string>();
        var transport = Transport(() => new FakeSmtpClient { OnHost = host => { hosts.Add(host); if (host == "mailhog.test") throw new IOException(); } });
        await RunAsync(database, transport);
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("UPDATE notifications SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Status\" = 'Failed'", Ct);
        await RunAsync(database, transport);
        Assert.Equal(1, hosts.Count(h => h == "gmail.test"));
        Assert.Equal(2, hosts.Count(h => h == "mailhog.test"));
    }

    [Fact]
    public async Task Falha_ao_gravar_Sent_repete_apenas_persistencia_e_depois_recupera_Unknown()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(Delivery());
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient { OnSend = () => { sends++; return Task.CompletedTask; } });
        await using (var db = database.Open())
        {
            var repository = RepositoryProxy.Wrap(new NotificationRepository(db));
            await Dispatcher(repository, transport).RunOnceAsync(Ct);
            Assert.Equal(3, ((RepositoryProxy)repository).SentAttempts);
            Assert.Equal(NotificationStatus.Sending, (await db.Notifications.SingleAsync(Ct)).Status);
            await ExpireAsync(db, id);
        }
        await RunAsync(database, transport);
        await using var verify = database.Open();
        Assert.Equal(NotificationStatus.Unknown, (await verify.Notifications.SingleAsync(Ct)).Status);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Destino_ausente_nao_faz_fallback_nem_conecta()
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InsertAsync(Delivery("removed"));
        await RunAsync(database, Transport(() => throw new InvalidOperationException("Must not create SMTP client")));
        await using var db = database.Open();
        Assert.Equal(NotificationStatus.Failed, (await db.Notifications.SingleAsync(Ct)).Status);
    }

    [Fact]
    public async Task Desabilitado_por_padrao_nao_resolve_banco_e_modo_ativo_bloqueia_envio_direto()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();
        using var worker = new NotificationsDispatcherWorker(provider.GetRequiredService<IServiceScopeFactory>(),
            new NotificationsDispatcherOptions(), NullLogger<NotificationsDispatcherWorker>.Instance);
        await worker.StartAsync(Ct);
        await worker.StopAsync(Ct);
        var configuration = Configuration();
        configuration["NotificationsDispatcher:Enabled"] = "true";
        var sender = new SmtpNotificationSender(configuration, NullLogger<SmtpNotificationSender>.Instance,
            () => throw new InvalidOperationException("Must not connect"));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => sender.SendAsync(new("recipient@example.test", "subject", "body"), Ct));
        Assert.Contains("Envio direto desabilitado", error.Message);
    }

    [Theory]
    [InlineData("250 2.0.0 queued recipient@example.test password body", "SMTP_ACCEPTED 2.0.0")]
    [InlineData("2.1.5 accepted <private@example.test>", "SMTP_ACCEPTED 2.1.5")]
    [InlineData("accepted private@example.test", "SMTP_ACCEPTED")]
    [InlineData("250 2.0.0private@example.test", "SMTP_ACCEPTED")]
    [InlineData("250 5.0.0 private", "SMTP_ACCEPTED")]
    public async Task MessageId_persistido_antes_do_envio_e_aceitacao_sanitizada(string response, string expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var delivery = Delivery();
        // Exercita também o INSERT SQL usado na intenção transacional.
        await using (var db = database.Open())
        {
            await using var transaction = await db.Database.BeginTransactionAsync(Ct);
            Assert.True(await new NotificationRepository(db).TryAddAsync(delivery, Ct));
            await transaction.CommitAsync(Ct);
        }
        var sends = 0;
        var transport = Transport(() => new FakeSmtpClient
        {
            Response = response,
            OnMessage = message => Assert.Equal(delivery.SmtpMessageId, message.MessageId),
            OnSend = async () =>
            {
                await using var db = database.Open();
                var persisted = await db.Notifications.SingleAsync(Ct);
                Assert.Equal(delivery.SmtpMessageId, persisted.SmtpMessageId);
                Assert.Equal(NotificationStatus.Sending, persisted.Status);
                Assert.Null(persisted.SmtpAcceptance);
                sends++;
            }
        });
        await RunAsync(database, transport);
        await RunAsync(database, transport);
        await using var verify = database.Open();
        var sent = await verify.Notifications.SingleAsync(Ct);
        Assert.Equal($"fcg.{delivery.Id:N}@notifications.invalid", sent.SmtpMessageId);
        Assert.Equal(expected, sent.SmtpAcceptance);
        Assert.NotNull(sent.SentConfirmationToken);
        Assert.Equal(1, sends);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    public async Task Commit_Sent_com_resposta_perdida_nao_gera_falso_negativo_nem_reenvio(int readFailures)
    {
        await using var database = await TestDatabase.CreateAsync();
        await database.InsertAsync(Delivery());
        var sends = 0;
        var logger = new RecordingDispatcherLogger();
        var transport = Transport(() => new FakeSmtpClient { OnSend = () => { sends++; return Task.CompletedTask; } });
        await using (var db = database.Open())
        {
            var repository = RepositoryProxy.Wrap(new NotificationRepository(db));
            var proxy = (RepositoryProxy)repository;
            proxy.LoseResponse = true;
            proxy.ReadFailures = readFailures;
            await new PersistentNotificationDispatcher(repository, transport,
                new NotificationsDispatcherOptions { Enabled = true }, logger).RunOnceAsync(Ct);
            Assert.Equal(Math.Min(3, readFailures + 1), proxy.SentAttempts);
            Assert.Contains(logger.Entries, e => Equals(e["Stage"], readFailures == 3 ? "SENT_CONFIRMATION_UNKNOWN" : "ACCEPTED_ALREADY_RECORDED"));
            Assert.DoesNotContain(logger.Entries, e => Equals(e["Stage"], "ACCEPTED_NOT_RECORDED"));
        }
        await RunAsync(database, transport);
        await using var verify = database.Open();
        Assert.Equal(NotificationStatus.Sent, (await verify.Notifications.SingleAsync(Ct)).Status);
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Recuperacao_preserva_MessageId_e_token_antigo_nao_confirma_outro_dispatcher()
    {
        await using var database = await TestDatabase.CreateAsync();
        var delivery = Delivery();
        await database.InsertAsync(delivery);
        await using var db = database.Open();
        var repo = new NotificationRepository(db);
        var old = (await repo.TryAcquireAsync(delivery.Id, TimeSpan.FromMinutes(5), Ct))!;
        await ExpireAsync(db, delivery.Id);
        await repo.RecoverExpiredAsync(Ct);
        var current = (await repo.TryAcquireAsync(delivery.Id, TimeSpan.FromMinutes(5), Ct))!;
        Assert.Equal(old.SmtpMessageId, current.SmtpMessageId);
        var token = current.ReservationToken!.Value;
        Assert.True(await repo.TryStartSendingAsync(delivery.Id, token, Ct));
        Assert.False(await repo.TryMarkSentAsync(delivery.Id, old.ReservationToken!.Value, Ct));
        Assert.Equal(SentConfirmation.StateChanged, await repo.ReadSentConfirmationAsync(delivery.Id, old.ReservationToken.Value, Ct));
        Assert.True(await repo.TryMarkSentAsync(delivery.Id, token, Ct));
        var before = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        Assert.False(await repo.TryMarkSentAsync(delivery.Id, token, Ct));
        Assert.Equal(SentConfirmation.ConfirmedByAttempt, await repo.ReadSentConfirmationAsync(delivery.Id, token, Ct));
        Assert.Equal(SentConfirmation.StateChanged, await repo.ReadSentConfirmationAsync(delivery.Id, old.ReservationToken.Value, Ct));
        var after = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(before.SentAt, after.SentAt);
        Assert.Equal(before.UpdatedAt, after.UpdatedAt);
    }

    [Fact]
    public async Task Aceitacao_apos_expiracao_preserva_Unknown_sem_segundo_envio()
    {
        await using var database = await TestDatabase.CreateAsync();
        var delivery = Delivery();
        await database.InsertAsync(delivery);
        var sends = 0;
        var logger = new RecordingDispatcherLogger();
        var transport = Transport(() => new FakeSmtpClient { OnSend = async () =>
        {
            sends++;
            await using var db = database.Open();
            await ExpireAsync(db, delivery.Id);
            await new NotificationRepository(db).RecoverExpiredAsync(Ct);
        }});
        await using (var db = database.Open())
            await new PersistentNotificationDispatcher(new NotificationRepository(db), transport,
                new NotificationsDispatcherOptions { Enabled = true }, logger).RunOnceAsync(Ct);
        await RunAsync(database, transport);
        await using var verify = database.Open();
        var result = await verify.Notifications.SingleAsync(Ct);
        Assert.Equal(NotificationStatus.Unknown, result.Status);
        Assert.Equal(delivery.SmtpMessageId, result.SmtpMessageId);
        Assert.Null(result.SentConfirmationToken);
        Assert.Null(result.SmtpAcceptance);
        Assert.Contains(logger.Entries, e => Equals(e["Stage"], "SENT_STATE_CHANGED"));
        Assert.Equal(1, sends);
    }

    [Fact]
    public async Task Migration_preenche_identificador_sem_inventar_aceitacao_ou_alterar_estados()
    {
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await using var db = database.Open();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261023004351_NotificationReconciliationAudit", Ct);
        var rows = new[] { NotificationStatus.Pending, NotificationStatus.Sent, NotificationStatus.Unknown }
            .Select(status => (Id: Guid.NewGuid(), Status: status)).ToArray();
        foreach (var row in rows)
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO notifications ("Id", "EventId", "CorrelationId", "UserId", "Type", "Destination",
                    "Status", "CreatedAt", "UpdatedAt", "BusinessOperationId", "LogicalRecipientId",
                    "SmtpDestinationId", "Subject", "Body", "AttemptCount")
                VALUES ({row.Id}, {Guid.NewGuid()}, {Guid.NewGuid()}, {Guid.NewGuid()}, 'Welcome', 'private@example.test',
                    {row.Status.ToString()}, clock_timestamp(), clock_timestamp(), {row.Id.ToString()}, 'user',
                    'gmail', 'subject', 'body', 0)
                """, Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        foreach (var row in rows)
        {
            var stored = await db.Notifications.AsNoTracking().SingleAsync(n => n.Id == row.Id, Ct);
            Assert.Equal(row.Status, stored.Status);
            Assert.Equal($"fcg.{row.Id:N}@notifications.invalid", stored.SmtpMessageId);
            Assert.Null(stored.SmtpAcceptance);
            Assert.Null(stored.SentConfirmationToken);
        }
        Assert.Equal(rows[0].Id, Assert.Single(await new NotificationRepository(db).GetEligibleIdsAsync(10, Ct)));
    }

    private static Notification Delivery(string route = "gmail") => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
        NotificationType.Welcome, "user-created-1", "user-1", route, "recipient@example.test", "subject", "body", DateTimeOffset.UtcNow);
    private static PersistentNotificationDispatcher Dispatcher(INotificationRepository repository, IPersistentSmtpTransport transport) =>
        new(repository, transport, new NotificationsDispatcherOptions { Enabled = true }, NullLogger<PersistentNotificationDispatcher>.Instance);
    private static async Task RunAsync(TestDatabase database, IPersistentSmtpTransport transport)
    {
        await using var db = database.Open();
        await Dispatcher(new NotificationRepository(db), transport).RunOnceAsync(Ct);
    }
    private static Task<int> ExpireAsync(NotificationsDbContext db, Guid id) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"ReservationExpiresAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {id}", Ct);
    private static PersistentSmtpTransport Transport(Func<ISmtpClient> factory) =>
        new(Configuration(), NullLogger<PersistentSmtpTransport>.Instance, factory);
    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Smtp:Destinations:0:Id"] = "mailhog", ["Smtp:Destinations:0:Host"] = "mailhog.test",
        ["Smtp:Destinations:0:Port"] = "1025", ["Smtp:Destinations:0:From"] = "sender@example.test",
        ["Smtp:Destinations:1:Id"] = "gmail", ["Smtp:Destinations:1:Host"] = "gmail.test",
        ["Smtp:Destinations:1:Port"] = "587", ["Smtp:Destinations:1:From"] = "sender@example.test"
    }).Build();

    private sealed class FakeSmtpClient : SmtpClient
    {
        public string? Failure { get; init; }
        public Func<Task>? OnConnect { get; init; }
        public Action<string>? OnHost { get; init; }
        public Func<Task>? OnSend { get; init; }
        public Action<MimeMessage>? OnMessage { get; init; }
        public string Response { get; init; } = "accepted";
        private bool _connected;
        public override bool IsConnected => _connected;
        public override async Task ConnectAsync(string host, int port, SecureSocketOptions options, CancellationToken cancellationToken = default)
        {
            OnHost?.Invoke(host);
            if (Failure == "connect") throw new IOException();
            _connected = true;
            if (OnConnect is not null) await OnConnect();
        }
        public override async Task<string> SendAsync(FormatOptions options, MimeMessage message, CancellationToken cancellationToken = default, ITransferProgress? progress = null)
        {
            OnMessage?.Invoke(message);
            if (OnSend is not null) await OnSend();
            if (Failure == "send") throw new IOException();
            return Response;
        }
        public override Task DisconnectAsync(bool quit, CancellationToken cancellationToken = default)
        {
            if (Failure == "disconnect") throw new IOException();
            _connected = false;
            return Task.CompletedTask;
        }
    }

    public class RepositoryProxy : DispatchProxy
    {
        private INotificationRepository? _inner;
        public bool LoseResponse { get; set; }
        public int ReadFailures { get; set; }
        public int SentAttempts { get; private set; }
        public static INotificationRepository Wrap(INotificationRepository inner)
        {
            var proxy = Create<INotificationRepository, RepositoryProxy>();
            ((RepositoryProxy)proxy)._inner = inner;
            return proxy;
        }
        private static async Task<bool> CommitThenLoseAsync(Task<bool> write)
        {
            var result = await write;
            if (result) throw new IOException("Commit response lost");
            return result;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotificationRepository.TryMarkSentAsync))
            {
                SentAttempts++;
                if (LoseResponse) return CommitThenLoseAsync((Task<bool>)targetMethod.Invoke(_inner, args)!);
                return Task.FromException<bool>(new IOException("Simulated persistence failure"));
            }
            if (targetMethod.Name == nameof(INotificationRepository.ReadSentConfirmationAsync) && ReadFailures > 0)
            {
                ReadFailures--;
                return Task.FromException<SentConfirmation>(new IOException("Read unavailable"));
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}
