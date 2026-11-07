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
        services.AddScoped<INotificationOperationsQuery, NotificationRepository>();
        services.AddScoped<INotificationReconciliation, NotificationRepository>();
        services.AddScoped<IUnitOfWork, UnitOfWork>();
        services.AddScoped<IUserEmailRepository, UserEmailRepository>();

        services.AddScoped<INotificationSender, SmtpNotificationSender>();
        var dispatcherOptions = configuration.GetSection("NotificationsDispatcher").Get<NotificationsDispatcherOptions>()
            ?? new NotificationsDispatcherOptions();
        if (dispatcherOptions.Enabled)
        {
            dispatcherOptions.Validate();
            ValidatePersistentSmtpDestinationIds(configuration);
        }

        services.AddSingleton(dispatcherOptions);
        services.AddScoped<IPersistentSmtpTransport, PersistentSmtpTransport>();
        services.AddScoped<PersistentNotificationDispatcher>();
        services.AddHostedService<NotificationsDispatcherWorker>();
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

                // Intervalos crescentes (~3,5 min no total): o PaymentProcessedEvent pode chegar
                // antes de o UserCreatedEvent ter gravado o e-mail do usuário na projeção local;
                // com poucos segundos de retry a compra ia para a fila _error sem recuperação.
                rabbitMqConfigurator.UseMessageRetry(retryConfigurator =>
                {
                    retryConfigurator.Intervals(
                        TimeSpan.FromSeconds(5),
                        TimeSpan.FromSeconds(15),
                        TimeSpan.FromSeconds(30),
                        TimeSpan.FromSeconds(60),
                        TimeSpan.FromMinutes(2));
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

    // Falha na subida (e não em cada mensagem, indo para a fila _error) quando o modo
    // persistente está ligado sem Id de destino SMTP explícito, único e estável.
    private static void ValidatePersistentSmtpDestinationIds(IConfiguration configuration)
    {
        var sections = configuration.GetSection("Smtp:Destinations").GetChildren().ToArray();
        if (sections.Length == 0)
        {
            sections = [configuration.GetSection("Smtp")];
        }

        var ids = sections.Select(section => section["Id"]).ToArray();
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 100)
            || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
        {
            throw new InvalidOperationException(
                "Com NotificationsDispatcher:Enabled=true, configure Smtp:Id (destino único) ou " +
                "Smtp:Destinations:N:Id, com Ids explícitos, únicos e de até 100 caracteres.");
        }
    }
}