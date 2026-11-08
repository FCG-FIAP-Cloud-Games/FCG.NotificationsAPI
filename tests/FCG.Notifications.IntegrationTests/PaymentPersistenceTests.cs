using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
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
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class PaymentPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Aprovado_reentrega_e_novo_evento_do_mesmo_pagamento_nao_duplicam()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await ProcessAsync(database, payment);
        await ProcessAsync(database, payment);
        await ProcessAsync(database, payment with { EventId = Guid.NewGuid(), Amount = 59.900m });
        await using var db = database.Open();
        var deliveries = await db.Notifications.ToListAsync(Ct);
        Assert.Equal(2, deliveries.Count);
        Assert.Equal(2, deliveries.Select(n => n.SmtpDestinationId).Distinct().Count());
        Assert.All(deliveries, n =>
        {
            Assert.Equal(NotificationStatus.Pending, n.Status);
            Assert.Equal(payment.PaymentId.ToString(), n.BusinessOperationId);
            Assert.Equal(payment.UserId.ToString(), n.LogicalRecipientId);
            Assert.Equal("buyer@example.test", n.Destination);
            Assert.Contains(payment.OrderId.ToString(), n.Body!);
            Assert.Equal(payment.CorrelationId, n.CorrelationId);
        });
        Assert.Equal(2, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Rejeitado_confirma_Inbox_sem_contato_nem_entregas()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment() with { Status = PaymentStatus.Rejected };
        await ProcessAsync(database, payment);
        await ProcessAsync(database, payment);
        await using var db = database.Open();
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Pagamentos_distintos_do_mesmo_usuario_geram_entregas_independentes()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await ProcessAsync(database, payment);
        await ProcessAsync(database, payment with { EventId = Guid.NewGuid(), PaymentId = Guid.NewGuid(), OrderId = Guid.NewGuid() });
        await using var db = database.Open();
        Assert.Equal(4, await db.Notifications.CountAsync(Ct));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concorrencia_protege_EventId_e_PaymentId(bool distinctEvents)
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProcessAsync(database,
            distinctEvents ? payment with { EventId = Guid.NewGuid() } : payment)));
        await using var db = database.Open();
        Assert.Equal(2, await db.Notifications.CountAsync(Ct));
        Assert.Equal(distinctEvents ? 8 : 1, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Contato_ausente_reverte_Inbox_e_permite_reprocessamento()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(database, payment));
        await using (var db = database.Open())
        {
            Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
            Assert.Empty(await db.Notifications.ToListAsync(Ct));
        }
        await ContactAsync(database, payment.UserId);
        await ProcessAsync(database, payment);
        await using var verify = database.Open();
        Assert.Equal(2, await verify.Notifications.CountAsync(Ct));
    }

    [Fact]
    public async Task Falha_no_commit_reverte_Inbox_e_todas_as_entregas()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications ADD CONSTRAINT fail_mailhog CHECK (\"SmtpDestinationId\" <> 'mailhog')", Ct);
        await Assert.ThrowsAsync<DbUpdateException>(() => ProcessAsync(database, payment));
        await using (var db = database.Open())
        {
            Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
            Assert.Empty(await db.Notifications.ToListAsync(Ct));
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE notifications DROP CONSTRAINT fail_mailhog", Ct);
        }
        await ProcessAsync(database, payment);
        await using var verify = database.Open();
        Assert.Equal(2, await verify.Notifications.CountAsync(Ct));
    }

    [Theory]
    [InlineData("amount")]
    [InlineData("user")]
    [InlineData("status")]
    [InlineData("order")]
    public async Task Conflitos_nao_sobrescrevem_entregas_nem_confirmam_Inbox(string field)
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await ProcessAsync(database, payment);
        var conflict = payment with { EventId = Guid.NewGuid() };
        conflict = field switch
        {
            "amount" => conflict with { Amount = 100m },
            "user" => conflict with { UserId = Guid.NewGuid() },
            "status" => conflict with { Status = PaymentStatus.Rejected },
            _ => conflict with { OrderId = Guid.NewGuid() }
        };
        await ContactAsync(database, conflict.UserId);
        await Assert.ThrowsAsync<PermanentMessageException>(() => ProcessAsync(database, conflict));
        await using var db = database.Open();
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
        Assert.Equal(2, await db.Notifications.CountAsync(Ct));
        Assert.All(await db.Notifications.ToListAsync(Ct), n => Assert.Equal(payment.EventId, n.EventId));
    }

    [Fact]
    public async Task Conflitos_concorrentes_do_mesmo_pagamento_tem_um_unico_vencedor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        async Task<bool> Attempt(PaymentProcessedEvent message)
        {
            try { await ProcessAsync(database, message); return true; }
            catch (PermanentMessageException) { return false; }
        }
        var results = await Task.WhenAll(Attempt(payment), Attempt(payment with { EventId = Guid.NewGuid(), Amount = 100m }));
        Assert.Equal(1, results.Count(value => value));
        await using var db = database.Open();
        Assert.Equal(2, await db.Notifications.CountAsync(Ct));
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Mudanca_de_contato_nao_altera_destinatario_congelado()
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment();
        await ContactAsync(database, payment.UserId);
        await ProcessAsync(database, payment);
        await using (var db = database.Open())
            await new UserEmailRepository(db).UpsertAsync(payment.UserId, "new@example.test", DateTimeOffset.UtcNow.AddDays(1), Ct);
        await ProcessAsync(database, payment with { EventId = Guid.NewGuid() });
        await using var verify = database.Open();
        Assert.All(await verify.Notifications.ToListAsync(Ct), n => Assert.Equal("buyer@example.test", n.Destination));
    }

    [Theory]
    [InlineData(PaymentStatus.Approved, 1)]
    [InlineData(PaymentStatus.Rejected, 0)]
    public async Task Legado_preserva_envio_direto_sem_Inbox_ou_Pending(PaymentStatus status, int sends)
    {
        await using var database = await TestDatabase.CreateAsync();
        var payment = Payment() with { Status = status };
        await ContactAsync(database, payment.UserId);
        var sender = new RecordingSender();
        await ProcessAsync(database, payment, false, sender);
        Assert.Equal(sends, sender.Sends);
        await using var db = database.Open();
        Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
        Assert.Empty(await db.Notifications.ToListAsync(Ct));
    }

    private static async Task ContactAsync(TestDatabase database, Guid userId)
    {
        await using var db = database.Open();
        await new UserEmailRepository(db).UpsertAsync(userId, "buyer@example.test", DateTimeOffset.UtcNow, Ct);
    }

    private static async Task ProcessAsync(TestDatabase database, PaymentProcessedEvent payment, bool enabled = true, RecordingSender? sender = null)
    {
        await using var db = database.Open();
        var smtp = sender ?? new RecordingSender { Reject = true };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Smtp:Destinations:0:Id"] = "gmail", ["Smtp:Destinations:1:Id"] = "mailhog"
        }).Build();
        using var provider = new ServiceCollection()
            .AddSingleton<IPurchaseConfirmationService>(new PurchaseConfirmationService(smtp, new UserEmailRepository(db)))
            .AddSingleton<ILogger<PaymentProcessedEventConsumer>>(NullLogger<PaymentProcessedEventConsumer>.Instance)
            .AddSingleton(new NotificationsDispatcherOptions { Enabled = enabled })
            .AddSingleton<IConfiguration>(configuration)
            .AddSingleton<IInboxStore>(new InboxStore(db))
            .AddSingleton<IUnitOfWork>(new UnitOfWork(db))
            .AddSingleton<INotificationRepository>(new NotificationRepository(db))
            .AddTransient<PaymentProcessedEventConsumer>().BuildServiceProvider(true);
        await provider.GetRequiredService<PaymentProcessedEventConsumer>().Consume(ContextProxy.Create(payment));
        if (enabled) Assert.Equal(0, smtp.Sends);
    }
    private static PaymentProcessedEvent Payment() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, 1,
        Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 59.90m, "BRL", PaymentStatus.Approved);

    private sealed class RecordingSender : INotificationSender
    {
        public bool Reject { get; init; }
        public int Sends { get; private set; }
        public Task SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Sends++;
            if (Reject) throw new InvalidOperationException("Persistent consumer must not send SMTP.");
            return Task.CompletedTask;
        }
    }
    public class ContextProxy : DispatchProxy
    {
        private PaymentProcessedEvent? _message;
        public static ConsumeContext<PaymentProcessedEvent> Create(PaymentProcessedEvent message)
        {
            var context = Create<ConsumeContext<PaymentProcessedEvent>, ContextProxy>();
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
