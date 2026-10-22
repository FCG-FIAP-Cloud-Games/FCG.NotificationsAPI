using FCG.Notifications.Application.Abstractions;

namespace FCG.Notifications.Infrastructure.Delivery;

public interface IPersistentSmtpTransport
{
    Task<IPreparedSmtpDelivery> PrepareAsync(string destinationId, NotificationMessage message, CancellationToken cancellationToken);
}

public interface IPreparedSmtpDelivery : IAsyncDisposable
{
    Task SendAsync(CancellationToken cancellationToken);
}
