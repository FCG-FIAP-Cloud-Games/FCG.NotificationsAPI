using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using FCG.Notifications.UnitTests.TestDoubles;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.UnitTests;

public class UserCreatedEventConsumerTests
{
    [Fact]
    public async Task Consome_evento_e_envia_notificacao()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IWelcomeEmailService, WelcomeEmailService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<UserCreatedEventConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var userCreatedEvent = new UserCreatedEvent
        (
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow,
            1,
            Guid.NewGuid(),
            "Leonardo",
            "leoloss@uorak.com"
        );

        await harness.Bus.Publish(userCreatedEvent, TestContext.Current.CancellationToken);

        Assert.True(await harness.Consumed.Any<UserCreatedEvent>(TestContext.Current.CancellationToken));
        Assert.Equal("leoloss@uorak.com", sender.LastMessage?.Recipient);
    }

    [Fact]
    public async Task Payload_invalido_nao_envia_notificacao()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IWelcomeEmailService, WelcomeEmailService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<UserCreatedEventConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var invalidEvent = new UserCreatedEvent
        (
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow,
            1,
            Guid.NewGuid(),
            "Leonardo",
            string.Empty
        );

        await harness.Bus.Publish(invalidEvent, TestContext.Current.CancellationToken);

        var consumerHarness = harness.GetConsumerHarness<UserCreatedEventConsumer>();
        Assert.True(await consumerHarness.Consumed.Any<UserCreatedEvent>(TestContext.Current.CancellationToken));
        Assert.Null(sender.LastMessage);
    }

    [Fact]
    public async Task Preserva_CorrelationId_do_evento_original()
    {
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IWelcomeEmailService, WelcomeEmailService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<UserCreatedEventConsumer>();
            })
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        var correlationId = Guid.NewGuid();
        var userCreatedEvent = new UserCreatedEvent
        (
            Guid.NewGuid(),
            correlationId,
            DateTime.UtcNow,
            1,
            Guid.NewGuid(),
            "Leonardo",
            "leoloss@uorak.com"
        );

        await harness.Bus.Publish(userCreatedEvent, TestContext.Current.CancellationToken);

        Assert.True(await harness.Consumed.Any<UserCreatedEvent>(TestContext.Current.CancellationToken));

        var consumed = harness.Consumed.Select<UserCreatedEvent>(TestContext.Current.CancellationToken).First();
        Assert.Equal(correlationId, consumed.Context.Message.CorrelationId);
    }

    [Fact]
    public async Task Falha_de_transicao_e_reprocessada_com_sucesso()
    {
        var sender = new FlakyNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddScoped<IWelcomeEmailService, WelcomeEmailService>()
            .AddMassTransitTestHarness(x =>
            {
                x.AddConsumer<UserCreatedEventConsumer>();
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

        var userCreatedEvent = new UserCreatedEvent
        (
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow,
            1,
            Guid.NewGuid(),
            "Leonardo",
            "leoloss@uorak.com"
        );

        await harness.Bus.Publish(userCreatedEvent, TestContext.Current.CancellationToken);

        Assert.True(await harness.Consumed.Any<UserCreatedEvent>(TestContext.Current.CancellationToken));
        Assert.Equal(2, sender.Attempts);
        Assert.NotNull(sender.LastMessage);
    }
}