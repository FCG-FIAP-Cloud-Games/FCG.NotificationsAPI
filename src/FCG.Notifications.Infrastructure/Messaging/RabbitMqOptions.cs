namespace FCG.Notifications.Infrastructure.Messaging;

/// <summary>
/// Credencial própria do NotificationsAPI para o broker, nunca o JWT do usuário. 
/// Valores preenchidos por variáveis de ambiente, secrets locais ou Kubernetes Secrets; nunca versionados.
/// </summary>
public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public string VirtualHost { get; init; } = "/";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
}