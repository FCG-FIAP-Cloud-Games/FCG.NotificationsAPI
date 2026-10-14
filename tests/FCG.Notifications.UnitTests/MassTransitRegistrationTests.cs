using FCG.Notifications.Infrastructure.IoC;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.UnitTests;

public class MassTransitRegistrationTests
{
    [Fact]
    public void AddNotificationsInfrastructure_registra_bus_sem_exigir_broker_disponivel()
    {
        var configuration = new ConfigurationBuilder().Build();
        var services = new ServiceCollection();

        services.AddNotificationsInfrastructure(configuration);

        using var provider = services.BuildServiceProvider();

        // Resolver o bus não deve exigir uma conexão real com o RabbitMQ:
        // a conexão é assíncrona e não bloqueia a resolução do serviço.
        var bus = provider.GetRequiredService<IBus>();

        Assert.NotNull(bus);
    }
}