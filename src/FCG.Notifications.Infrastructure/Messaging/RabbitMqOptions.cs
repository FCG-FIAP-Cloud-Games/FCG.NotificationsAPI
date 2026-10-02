namespace FCG.Notifications.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";
    public string host { get; init; } = "localhost";
    public string virtualHost { get; init; } = "/";
    public string userName { get; init; } = string.Empty;
    public string password { get; init; } = string.Empty;
}   