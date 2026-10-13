namespace FCG.Notifications.Infrastructure.Messaging;

public sealed class RabbitMqOptions
{
    public const string SectionName = "RabbitMq";

    public string Host { get; init; } = "localhost";
    public string VirtualHost { get; init; } = "/";
    public string Username { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;

    /// <summary>
    /// Falha na inicialização com mensagem objetiva, em vez do UriFormatException
    /// que o MassTransit lança lá no fundo quando o Host chega vazio/inválido.
    /// </summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Host) ||
            Uri.CheckHostName(Host) == UriHostNameType.Unknown)
        {
            throw new InvalidOperationException(
                $"RabbitMq:Host inválido ('{Host}'). Informe só o nome do host (ex.: rabbitmq), " +
                "sem protocolo (amqp://) nem porta. Variável de ambiente: RabbitMq__Host (dois underscores).");
        }

        if (string.IsNullOrWhiteSpace(VirtualHost))
        {
            throw new InvalidOperationException(
                "RabbitMq:VirtualHost vazio. Use '/' ou o nome do vhost. Variável: RabbitMq__VirtualHost.");
        }
    }
}