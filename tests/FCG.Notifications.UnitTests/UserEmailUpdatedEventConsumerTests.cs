using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using FCG.Notifications.UnitTests.TestDoubles;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.UnitTests;

public class UserEmailUpdatedEventConsumerTests
{
    [Fact]
    public async Task Atualiza_email_da_projecao_por_UserId()
    {
        var userId = Guid.NewGuid();
        var repository = new InMemoryUserEmailRepository();
        await repository.UpsertAsync(userId, "email-antigo@example.net", DateTimeOffset.UtcNow.AddDays(-1), CancellationToken.None);

        await using var provider = new ServiceCollection()
            .AddSingleton<IUserEmailRepository>(repository)
            .AddMassTransitTestHarness(x => x.AddConsumer<UserEmailUpdatedEventConsumer>())
            .BuildServiceProvider(true);

        var harness = provider.GetTestHarness();
        await harness.Start();

        await harness.Bus.Publish(new UserEmailUpdatedEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            userId,
            "email-novo@example.net"), harness.CancellationToken);

        Assert.True(await harness.Consumed.Any<UserEmailUpdatedEvent>(harness.CancellationToken));
        Assert.Equal("email-novo@example.net", await repository.GetEmailAsync(userId, CancellationToken.None));
    }
}
