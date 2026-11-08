using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using FCG.Notifications.UnitTests.TestDoubles;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.UnitTests;

public class PurchaseNotificationTestEventConsumerTests
{
    [Fact]
    public async Task Usa_email_informado_no_evento_de_teste()
    {
        const string recipient = "membro-variavel+fcg@example.net";
        var sender = new RecordingNotificationSender();

        await using var provider = new ServiceCollection()
            .AddSingleton<INotificationSender>(sender)
            .AddMassTransitTestHarness(x => x.AddConsumer<PurchaseNotificationTestEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish(new PurchaseNotificationTestEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            59.90m,
            "BRL",
            recipient), harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<PurchaseNotificationTestEvent>(harness.CancellationToken));
        Assert.Equal(recipient, sender.LastMessage?.Recipient);
    }
}
