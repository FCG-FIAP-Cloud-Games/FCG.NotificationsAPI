using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Infrastructure.Delivery;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
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
        // Registra a implementação concreta do INotificationSender na infraestrutura
        services.AddScoped<INotificationSender, ConsoleNotificationSender>();

        var rabbitMqOptions = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>()
            ?? new RabbitMqOptions();

        // Não bloquear o startup do host esperando o broker responder: a credencial é própria deste serviço,
        // e o /health deste card não depende da conexão com o broker.
        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = false;
        });

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.AddConsumer<UserCreatedEventConsumer>();

            busConfigurator.UsingRabbitMq((context, rabbitMqConfigurator) =>
            {
                rabbitMqConfigurator.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, host =>
                {
                    host.Username(rabbitMqOptions.Username);
                    host.Password(rabbitMqOptions.Password);
                });

                // Retry para falhas transitórias: 
                // até 3 tentativas, intervalo fixo de 5s. PermanentMessageException nunca
                // é retida — vai direto para a fila de erro (notifications-user-created_error).
                rabbitMqConfigurator.UseMessageRetry(retryConfigurator =>
                {
                    retryConfigurator.Interval(3, TimeSpan.FromSeconds(5));
                    retryConfigurator.Ignore<PermanentMessageException>();
                });

                // Nome de fila explícito, seguindo a convenção <service>-<evento>
                // definida — não o nome automático do MassTransit.
                rabbitMqConfigurator.ReceiveEndpoint("notifications-user-created", endpointConfigurator =>
                {
                    endpointConfigurator.ConfigureConsumer<UserCreatedEventConsumer>(context);
                });
            });
        });

        return services;
    }
}