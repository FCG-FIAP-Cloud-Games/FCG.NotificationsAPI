using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Infrastructure.Data;
using FCG.Notifications.Infrastructure.Delivery;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace FCG.Notifications.Infrastructure.IoC;

public static class DependencyInjection
{
    private const string DatabaseConnectionName = "NotificationsDatabase";

    public static IServiceCollection AddNotificationsInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        var rabbitMqOptions = configuration.GetSection(RabbitMqOptions.SectionName).Get<RabbitMqOptions>()
            ?? new RabbitMqOptions();

        // Falha na subida com mensagem clara se o Host/VirtualHost vier vazio ou inválido
        // (ex.: variável RabbitMq__Host não definida no container), em vez do
        // UriFormatException que o MassTransit lança mais adiante.
        rabbitMqOptions.Validate();

        // Connection string própria do serviço (Card 28, item 11). A leitura fica dentro
        // da lambda: o erro só aparece quando o contexto é usado, então o host (e o /health)
        // sobem mesmo sem banco configurado.
        services.AddDbContext<NotificationsDbContext>(options =>
            options.UseNpgsql(
                configuration.GetConnectionString(DatabaseConnectionName)
                    ?? throw new InvalidOperationException(
                        $"Connection string '{DatabaseConnectionName}' não configurada.")));

        services.AddScoped<IInboxStore, InboxStore>();
        services.AddScoped<INotificationRepository, NotificationRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserEmailRepository, UserEmailRepository>();

        services.AddScoped<INotificationSender, SmtpNotificationSender>();
        services.AddScoped<IWelcomeEmailService, WelcomeEmailService>();
        services.AddScoped<IPurchaseConfirmationService, PurchaseConfirmationService>();

        var enablePurchaseNotificationTestEndpoint = configuration.GetValue<bool>(
            "Messaging:EnablePurchaseNotificationTestEndpoint");

        services.Configure<MassTransitHostOptions>(options =>
        {
            options.WaitUntilStarted = false;
        });

        services.AddMassTransit(busConfigurator =>
        {
            busConfigurator.AddConsumer<UserCreatedEventConsumer>();
            busConfigurator.AddConsumer<UserEmailUpdatedEventConsumer>();
            busConfigurator.AddConsumer<PaymentProcessedEventConsumer>();
            if (enablePurchaseNotificationTestEndpoint)
                busConfigurator.AddConsumer<PurchaseNotificationTestEventConsumer>();

            busConfigurator.UsingRabbitMq((context, rabbitMqConfigurator) =>
            {
                rabbitMqConfigurator.Host(rabbitMqOptions.Host, rabbitMqOptions.VirtualHost, host =>
                {
                    host.Username(rabbitMqOptions.Username);
                    host.Password(rabbitMqOptions.Password);
                });

                // Nome do exchange fixado pela convenção do Card 06 (#49, seção 3) — não o
                // namespace completo do tipo .NET.
                rabbitMqConfigurator.Message<UserCreatedEvent>(m => m.SetEntityName("UserCreatedEvent"));
                rabbitMqConfigurator.Message<PaymentProcessedEvent>(m => m.SetEntityName("PaymentProcessedEvent"));
                rabbitMqConfigurator.Message<UserEmailUpdatedEvent>(m => m.SetEntityName("UserEmailUpdatedEvent"));
                if (enablePurchaseNotificationTestEndpoint)
                {
                    rabbitMqConfigurator.Message<PurchaseNotificationTestEvent>(
                        m => m.SetEntityName("PurchaseNotificationTestEvent"));
                }

                rabbitMqConfigurator.UseMessageRetry(retryConfigurator =>
                {
                    retryConfigurator.Interval(3, TimeSpan.FromSeconds(5));
                    retryConfigurator.Ignore<PermanentMessageException>();
                });

                rabbitMqConfigurator.ReceiveEndpoint("notifications-user-created", endpointConfigurator =>
                {
                    // Permite publicar JSON simples pelo RabbitMQ Management para testes
                    // e integrações externas. O consumer deste endpoint recebe apenas
                    // UserCreatedEvent, então o raw deserializer pode inferir o tipo.
                    endpointConfigurator.UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true);
                    endpointConfigurator.ConfigureConsumer<UserCreatedEventConsumer>(context);
                });

                rabbitMqConfigurator.ReceiveEndpoint("notifications-user-email-updated", endpointConfigurator =>
                {
                    endpointConfigurator.UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true);
                    endpointConfigurator.ConfigureConsumer<UserEmailUpdatedEventConsumer>(context);
                });

                rabbitMqConfigurator.ReceiveEndpoint("notifications-payment-processed", endpointConfigurator =>
                {
                    // Permite publicar JSON simples pelo RabbitMQ Management durante
                    // testes locais; o contrato permanece igual ao PaymentsAPI.
                    endpointConfigurator.UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true);
                    endpointConfigurator.ConfigureConsumer<PaymentProcessedEventConsumer>(context);
                });

                if (enablePurchaseNotificationTestEndpoint)
                {
                    rabbitMqConfigurator.ReceiveEndpoint("notifications-purchase-test", endpointConfigurator =>
                    {
                        endpointConfigurator.UseRawJsonDeserializer(RawSerializerOptions.All, isDefault: true);
                        endpointConfigurator.ConfigureConsumer<PurchaseNotificationTestEventConsumer>(context);
                    });
                }
            });
        });

        return services;
    }
}
