using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using FCG.Notifications.Infrastructure.Delivery;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class PurchaseTestPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Primeiro_processamento_e_reentrega_congelam_uma_entrega_por_destino()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await ProcessAsync(database, message);
        await ProcessAsync(database, message);
        await using var db = database.Open();
        var deliveries = await db.Notifications.ToListAsync(Ct);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(2, deliveries.Select(n => n.SmtpDestinationId).Distinct().Count());
        Assert.All(deliveries, n =>
        {
            Assert.Equal(NotificationStatus.Pending, n.Status);
            Assert.Equal(NotificationType.PurchaseConfirmationTest, n.Type);
            Assert.Equal(message.EventId.ToString(), n.BusinessOperationId);
            Assert.Equal(message.UserId.ToString(), n.LogicalRecipientId);
            Assert.Equal(message.Email, n.Destination);
            Assert.Equal(message.CorrelationId, n.CorrelationId);
            Assert.Equal("Teste de confirmação de compra FCG", n.Subject);
            Assert.Equal($"Teste de envio para o pedido {message.OrderId}. Jogo: {message.GameId}, Valor: {message.Amount} {message.Currency}.", n.Body);
        });
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concorrencia_deduplica_reentrega_e_preserva_eventos_distintos(bool distinct)
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProcessAsync(database,
            distinct ? message with { EventId = Guid.NewGuid() } : message)));
        await using var db = database.Open();
        Assert.Equal(distinct ? 16 : 2, await db.Notifications.CountAsync(Ct));
        Assert.Equal(distinct ? 8 : 1, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Falha_na_segunda_entrega_reverte_Inbox_e_primeira_entrega_e_permite_retry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications ADD CONSTRAINT fail_mailhog CHECK (\"SmtpDestinationId\" <> 'mailhog')", Ct);
        await Assert.ThrowsAsync<PostgresException>(() => ProcessAsync(database, message));
        await using (var db = database.Open())
        {
            Assert.Empty(await db.Notifications.ToListAsync(Ct));
            Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications DROP CONSTRAINT fail_mailhog", Ct);
        }
        await ProcessAsync(database, message);
        await using var verify = database.Open();
        Assert.Equal(2, await verify.Notifications.CountAsync(Ct));
        Assert.Equal(1, await verify.InboxMessages.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destinatario_invalido_nao_persiste_nem_envia(bool enabled)
    {
        await using var database = await TestDatabase.CreateAsync();
        var sender = new RecordingSender();
        await Assert.ThrowsAsync<PermanentMessageException>(() => ProcessAsync(database,
            Message() with { Email = "invalid" }, enabled, sender));
        Assert.Null(sender.LastMessage);
        await using var db = database.Open();
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task EventId_vazio_e_rejeitado_sem_gerar_identificador_substituto()
    {
        await using var database = await TestDatabase.CreateAsync();
        await Assert.ThrowsAsync<PermanentMessageException>(() => ProcessAsync(database, Message() with { EventId = Guid.Empty }));
        await using var db = database.Open();
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Legado_preserva_conteudo_e_nao_cria_Inbox_ou_Pending()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        var sender = new RecordingSender();
        await ProcessAsync(database, message, false, sender);
        Assert.Equal(new NotificationMessage(message.Email, "Teste de confirmação de compra FCG",
            $"Teste de envio para o pedido {message.OrderId}. Jogo: {message.GameId}, Valor: {message.Amount} {message.Currency}."), sender.LastMessage);
        await using var db = database.Open();
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Destinos_invalidos_revertem_reserva(bool duplicate)
    {
        await using var database = await TestDatabase.CreateAsync();
        var config = Configuration();
        config["Smtp:Destinations:1:Id"] = duplicate ? "gmail" : "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(database, Message(), configuration: config));
        await using var db = database.Open();
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
    }

    private static async Task ProcessAsync(TestDatabase database, PurchaseNotificationTestEvent message,
        bool enabled = true, RecordingSender? sender = null, IConfiguration? configuration = null)
    {
        await using var db = database.Open();
        var smtp = sender ?? new RecordingSender { Reject = true };
        using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(smtp)
            .AddSingleton<ILogger<PurchaseNotificationTestEventConsumer>>(NullLogger<PurchaseNotificationTestEventConsumer>.Instance)
            .AddSingleton(new NotificationsDispatcherOptions { Enabled = enabled })
            .AddSingleton<IConfiguration>(configuration ?? Configuration())
            .AddSingleton<IInboxStore>(new InboxStore(db))
            .AddSingleton<IUnitOfWork>(new UnitOfWork(db))
            .AddSingleton<INotificationRepository>(new NotificationRepository(db))
            .AddTransient<PurchaseNotificationTestEventConsumer>().BuildServiceProvider(true);
        await provider.GetRequiredService<PurchaseNotificationTestEventConsumer>().Consume(ContextProxy.Create(message));
        if (enabled) Assert.Null(smtp.LastMessage);
    }
    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Smtp:Destinations:0:Id"] = "gmail", ["Smtp:Destinations:1:Id"] = "mailhog"
    }).Build();
    private static PurchaseNotificationTestEvent Message() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        1, Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 59.90m, "BRL", "recipient@example.test");
    private sealed class RecordingSender : INotificationSender
    {
        public bool Reject { get; init; }
        public NotificationMessage? LastMessage { get; private set; }
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            LastMessage = message;
            if (Reject) throw new InvalidOperationException("Persistent mode must not send SMTP.");
            return Task.CompletedTask;
        }
    }
    public class ContextProxy : DispatchProxy
    {
        private PurchaseNotificationTestEvent? _message;
        public static ConsumeContext<PurchaseNotificationTestEvent> Create(PurchaseNotificationTestEvent message)
        {
            var context = Create<ConsumeContext<PurchaseNotificationTestEvent>, ContextProxy>();
            ((ContextProxy)context)._message = message;
            return context;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Message" => _message,
            "get_CancellationToken" => Ct,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }
}
