using FCG.Notifications.Infrastructure.Messaging;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Notifications.Infrastructure.IoC;

public static class DependencyInjection
{
    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services, 
        IConfiguration configuration)
    {
        var rabbitMqOptions = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>() ?? new RabbitMqOptions();

        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = false;
        });

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.UsingRabbitMq((context, rabbitMqConfigurator) =>
            {
                rabbitMqConfigurator.Host(rabbitMqOptions.host, rabbitMqOptions.virtualHost, host =>
                {
                    host.Username(rabbitMqOptions.userName);
                    host.Password(rabbitMqOptions.password);
                });

                rabbitMqConfigurator.ConfigureEndpoints(context);
            });
        });

        return services;
    }


}