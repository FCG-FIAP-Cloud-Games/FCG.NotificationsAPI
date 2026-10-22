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
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MimeKit;
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class PersistentDispatcherTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
            if (OnSend is not null) await OnSend();
            if (Failure == "send") throw new IOException();
            return "accepted";
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
        public int SentAttempts { get; private set; }
        public static INotificationRepository Wrap(INotificationRepository inner)
        {
            var proxy = Create<INotificationRepository, RepositoryProxy>();
            ((RepositoryProxy)proxy)._inner = inner;
            return proxy;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == nameof(INotificationRepository.TryMarkSentAsync))
            {
                SentAttempts++;
                return Task.FromException<bool>(new IOException("Simulated persistence failure"));
            }
            return targetMethod.Invoke(_inner, args);
        }
    }
}
