using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using FCG.Notifications.Infrastructure.Delivery;
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

public class WelcomePersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Primeiro_evento_e_reentrega_persistem_uma_entrega_por_destino_sem_SMTP()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await ProcessAsync(database, message);
        await ProcessAsync(database, message);
        await using var db = database.Open();
        var deliveries = await db.Notifications.OrderBy(n => n.SmtpDestinationId).ToListAsync(Ct);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal("gmail", deliveries[0].SmtpDestinationId);
        Assert.Equal("mailhog", deliveries[1].SmtpDestinationId);
        Assert.All(deliveries, n =>
        {
            Assert.Equal(NotificationStatus.Pending, n.Status);
            Assert.Equal(NotificationType.Welcome, n.Type);
            Assert.Equal(message.UserId.ToString(), n.BusinessOperationId);
            Assert.Equal(message.UserId.ToString(), n.LogicalRecipientId);
            Assert.Equal(message.Email, n.Destination);
            Assert.Equal(message.CorrelationId, n.CorrelationId);
            Assert.Contains(message.Name, n.Body!);
            Assert.Equal("Bem-vindo à FCG!", n.Subject);
        });
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
        Assert.Equal(message.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
    }

    [Fact]
    public async Task Novo_EventId_atualiza_contato_sem_substituir_boas_vindas_congeladas()
    {
        await using var database = await TestDatabase.CreateAsync();
        var original = Message();
        await ProcessAsync(database, original);
        var updated = original with { EventId = Guid.NewGuid(), OccurredAt = original.OccurredAt.AddMinutes(1), Email = "new@example.test", Name = "New name" };
        await ProcessAsync(database, updated);
        await ProcessAsync(database, original with { EventId = Guid.NewGuid() });
        await using var db = database.Open();
        Assert.Equal(updated.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
        var deliveries = await db.Notifications.ToListAsync(Ct);
        Assert.Equal(2, deliveries.Count);
        Assert.All(deliveries, n =>
        {
            Assert.Equal(original.Email, n.Destination);
            Assert.Equal(original.EventId, n.EventId);
            Assert.Contains(original.Name, n.Body!);
        });
        Assert.Equal(3, await db.InboxMessages.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concorrencia_deduplica_evento_e_operacao_de_negocio(bool distinctEvents)
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ProcessAsync(database, distinctEvents
            ? message with { EventId = Guid.NewGuid(), OccurredAt = message.OccurredAt.AddMinutes(i), Email = $"user{i}@example.test" }
            : message)));
        await using var db = database.Open();
        Assert.Equal(2, await db.Notifications.CountAsync(Ct));
        Assert.Equal(distinctEvents ? 8 : 1, await db.InboxMessages.CountAsync(Ct));
        Assert.Equal(distinctEvents ? "user7@example.test" : message.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
    }

    [Fact]
    public async Task Falha_na_segunda_entrega_reverte_contato_Inbox_e_primeira_entrega()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await using (var db = database.Open())
        {
            await new UserEmailRepository(db).UpsertAsync(message.UserId, "previous@example.test", message.OccurredAt.AddDays(-1), Ct);
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications ADD CONSTRAINT fail_second_route CHECK (\"SmtpDestinationId\" <> 'mailhog')", Ct);
        }
        await Assert.ThrowsAsync<PostgresException>(() => ProcessAsync(database, message));
        await using (var verify = database.Open())
        {
            Assert.Empty(await verify.InboxMessages.ToListAsync(Ct));
            Assert.Empty(await verify.Notifications.ToListAsync(Ct));
            Assert.Equal("previous@example.test", (await verify.UserEmailContacts.SingleAsync(Ct)).Email);
            await verify.Database.ExecuteSqlRawAsync("ALTER TABLE notifications DROP CONSTRAINT fail_second_route", Ct);
        }
        await ProcessAsync(database, message);
        await using var final = database.Open();
        Assert.Equal(2, await final.Notifications.CountAsync(Ct));
        Assert.Equal(1, await final.InboxMessages.CountAsync(Ct));
        Assert.Equal(message.Email, (await final.UserEmailContacts.SingleAsync(Ct)).Email);
    }

    [Fact]
    public async Task Modo_legado_envia_diretamente_e_nao_deixa_Pending_ou_Inbox()
    {
        await using var database = await TestDatabase.CreateAsync();
        var sender = new RecordingSender();
        await ProcessAsync(database, Message(), enabled: false, sender: sender);
        Assert.Equal(1, sender.Sends);
        await using var db = database.Open();
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
        Assert.Equal(1, await db.UserEmailContacts.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Ids_ausentes_ou_duplicados_revertem_transacao(bool duplicate)
    {
        await using var database = await TestDatabase.CreateAsync();
        var configuration = Configuration();
        configuration["Smtp:Destinations:1:Id"] = duplicate ? "gmail" : "";
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(database, Message(), configuration: configuration));
        await using var db = database.Open();
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
        Assert.Empty(await db.UserEmailContacts.ToListAsync(Ct));
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Configuracao_de_destino_unico_usa_Id_explicito()
    {
        await using var database = await TestDatabase.CreateAsync();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Smtp:Id"] = "single" }).Build();
        await ProcessAsync(database, Message(), configuration: config);
        await using var db = database.Open();
        Assert.Equal("single", (await db.Notifications.SingleAsync(Ct)).SmtpDestinationId);
    }

    private static async Task ProcessAsync(TestDatabase database, UserCreatedEvent message, bool enabled = true,
        RecordingSender? sender = null, IConfiguration? configuration = null)
    {
        await using var db = database.Open();
        var smtp = sender ?? new RecordingSender { Reject = true };
        using var provider = new ServiceCollection()
            .AddSingleton<IWelcomeEmailService>(new WelcomeEmailService(smtp))
            .AddSingleton<IUserEmailRepository>(new UserEmailRepository(db))
            .AddSingleton<ILogger<UserCreatedEventConsumer>>(NullLogger<UserCreatedEventConsumer>.Instance)
            .AddSingleton(new NotificationsDispatcherOptions { Enabled = enabled })
            .AddSingleton<IConfiguration>(configuration ?? Configuration())
            .AddSingleton<IInboxStore>(new InboxStore(db))
            .AddSingleton<IUnitOfWork>(new UnitOfWork(db))
            .AddSingleton<INotificationRepository>(new NotificationRepository(db))
            .AddTransient<UserCreatedEventConsumer>()
            .BuildServiceProvider(true);
        var consumer = provider.GetRequiredService<UserCreatedEventConsumer>();
        await consumer.Consume(ContextProxy.Create(message));
        if (enabled) Assert.Equal(0, smtp.Sends);
    }

    private static IConfigurationRoot Configuration() => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Smtp:Destinations:0:Id"] = "gmail", ["Smtp:Destinations:1:Id"] = "mailhog"
    }).Build();
    private static UserCreatedEvent Message() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        1, Guid.NewGuid(), "Original name", "original@example.test");

    private sealed class RecordingSender : INotificationSender
    {
        public int Sends { get; private set; }
        public bool Reject { get; init; }
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Sends++;
            if (Reject) throw new InvalidOperationException("Persistent mode must not send directly.");
            return Task.CompletedTask;
        }
    }

    public class ContextProxy : DispatchProxy
    {
        private UserCreatedEvent? _message;
        public static ConsumeContext<UserCreatedEvent> Create(UserCreatedEvent message)
        {
            var context = Create<ConsumeContext<UserCreatedEvent>, ContextProxy>();
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
