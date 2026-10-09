using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
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
        var rabbitMqOptions = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>()
            ?? new RabbitMqOptions();

        services.AddScoped<INotificationSender, ConsoleNotificationSender>();
        services.AddScoped<IWelcomeEmailService, WelcomeEmailService>();
        services.AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>();

        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = false;
        });

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.AddConsumer<UserCreatedEventConsumer>();
            busConfigurator.AddConsumer<PaymentProcessedEventConsumer>();

            busConfigurator.UsingRabbitMq((context, rabbitMqConfigurator) =>
            {
                rabbitMqConfigurator.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, host =>
                {
                    host.Username(rabbitMqOptions.Username);
                    host.Password(rabbitMqOptions.Password);
                });

                // Nome do exchange fixado pela convenção do Card 06 (#49, seção 3) — não o
                // namespace completo do tipo .NET. Garante que o exchange seja o mesmo
                // independente de como cada serviço nomeia seu próprio contrato.
                rabbitMqConfigurator.Message<UserCreatedEvent>(m => m.SetEntityName("UserCreatedEvent"));
                rabbitMqConfigurator.Message<PaymentProcessedEvent>(m => m.SetEntityName("PaymentProcessedEvent"));

                rabbitMqConfigurator.UseMessageRetry(retryConfigurator =>
                {
                    retryConfigurator.Interval(3, TimeSpan.FromSeconds(5));
                    retryConfigurator.Ignore<PermanentMessageException>();
                });

                rabbitMqConfigurator.ReceiveEndpoint("notifications-user-created", endpointConfigurator =>
                {
                    endpointConfigurator.ConfigureConsumer<UserCreatedEventConsumer>(context);
                });

                rabbitMqConfigurator.ReceiveEndpoint("notifications-payment-processed", endpointConfigurator =>
                {
                    endpointConfigurator.ConfigureConsumer<PaymentProcessedEventConsumer>(context);
                });
            });
        });

        return services;
    }
}