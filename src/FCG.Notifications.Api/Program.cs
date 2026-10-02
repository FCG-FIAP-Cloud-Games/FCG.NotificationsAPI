using FCG.Notifications.Infrastructure.IoC;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddNotificationsInfrastructure(builder.Configuration);
builder.Services.AddProblemDetails();
builder.Services.AddHealthChecks();

var app = builder.Build();

app.UseExceptionHandler();
app.UseStatusCodePages();

app.MapHealthChecks("/health");

app.Run();

public partial class Program;