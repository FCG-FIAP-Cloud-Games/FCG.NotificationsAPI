using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Infrastructure.IoC;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

// Registra os serviços da camada de aplicação
builder.Services.AddScoped<IWelcomeEmailService, WelcomeEmailService>();

// Registra os serviços de infraestrutura e mensageria (já incluindo o INotificationSender)
builder.Services.AddNotificationsInfrastructure(builder.Configuration);

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

// Neste card, /health só confirma que o processo está ativo. O MassTransit registra
// automaticamente um health check próprio que depende do broker estar acessível —
// ainda não existe infraestrutura de RabbitMQ provisionada, então esse check é
// explicitamente ignorado aqui. Os checks de RabbitMQ e banco entram em cards futuros.
app.MapHealthChecks("/health", new HealthCheckOptions
{
    Predicate = _ => false
});

app.Run();

public partial class Program;