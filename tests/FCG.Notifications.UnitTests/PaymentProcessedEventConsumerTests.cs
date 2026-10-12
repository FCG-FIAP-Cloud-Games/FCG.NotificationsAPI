using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using FCG.Notifications.UnitTests.TestDoubles;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.UnitTests;

public class PaymentProcessedEventConsumerTests
{
    private static PaymentProcessedEvent CreateEvent(
        PaymentStatus status = PaymentStatus.Approved,
        Guid? correlationId = null,
        Guid? orderId = null,
        Guid? gameId = null) => new
        (
            Guid.NewGuid(),
            correlationId ?? Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            Guid.NewGuid(),
            orderId ?? Guid.NewGuid(),
            Guid.NewGuid(),
            gameId ?? Guid.NewGuid(),
            59.90m,
            "BRL",
            status
        );

    [Fact]
    public async Task Approved_gera_confirmacao_de_compra()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<PaymentProcessedEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var orderId = Guid.NewGuid();
        var paymentProcessedEvent = CreateEvent(PaymentStatus.Approved, orderId: orderId);

        await harness.Bus.Publish(paymentProcessedEvent, harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<PaymentProcessedEvent>(harness.CancellationToken));
        Assert.NotNull(sender.LastMessage);
        Assert.Contains(orderId.ToString(), sender.LastMessage.Body);
    }

    [Fact]
    public async Task Rejected_nao_gera_confirmacao_de_compra()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<PaymentProcessedEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var paymentProcessedEvent = CreateEvent(PaymentStatus.Rejected);

        await harness.Bus.Publish(paymentProcessedEvent, harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<PaymentProcessedEvent>(harness.CancellationToken));
        Assert.Null(sender.LastMessage);
    }

    [Fact]
    public async Task Payload_invalido_nao_envia_confirmacao()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<PaymentProcessedEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var invalidEvent = CreateEvent() with { GameId = Guid.Empty };

        await harness.Bus.Publish(invalidEvent, harness.CancellationToken);

        var consumerHarness = harness.GetConsumerHarness<PaymentProcessedEventConsumer>();
        Assert.True(await consumerHarness.Consumed.Any<PaymentProcessedEvent>(harness.CancellationToken));
        Assert.Null(sender.LastMessage);
    }

    [Fact]
    public async Task Preserva_CorrelationId_do_evento_original()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>()
            .AddMassTransitTestHarness(x => x.AddConsumer<PaymentProcessedEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var correlationId = Guid.NewGuid();
        var paymentProcessedEvent = CreateEvent(correlationId: correlationId);

        await harness.Bus.Publish(paymentProcessedEvent, harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<PaymentProcessedEvent>(harness.CancellationToken));

        var consumed = harness.Consumed.Select<PaymentProcessedEvent>(TestContext.Current.CancellationToken).First();
        Assert.Equal(correlationId, consumed.Context.Message.CorrelationId);
    }

    [Fact]
    public async Task Falha_transitoria_e_reprocessada_com_sucesso()
    {
        var sender = new FlakyNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<PaymentProcessedEventConsumer>();
                x.UsingInMemory((context, cfg) =>
                {
                    cfg.UseMessageRetry(retry =>
                    {
                        retry.Interval(3, TimeSpan.FromMilliseconds(10));
                        retry.Ignore<PermanentMessageException>();
                    });

                    cfg.ConfigureEndpoints(context);
                });
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var paymentProcessedEvent = CreateEvent(PaymentStatus.Approved);

        await harness.Bus.Publish(paymentProcessedEvent, harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<PaymentProcessedEvent>(harness.CancellationToken));
        Assert.Equal(2, sender.Attempts);
        Assert.NotNull(sender.LastMessage);
    }
}