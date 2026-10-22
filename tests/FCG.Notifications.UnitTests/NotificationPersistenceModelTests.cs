using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.UnitTests;

public class NotificationPersistenceModelTests
{
    [Fact]
    public void Intencao_congela_conteudo_e_identificadores_sem_reserva_inicial()
    {
        var now = DateTimeOffset.UtcNow;
        var notification = new Notification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.PurchaseConfirmation, "payment-1", "user-1", "gmail",
            "recipient@example.test", "subject", "body", now);
        Assert.Equal("payment-1", notification.BusinessOperationId);
        Assert.Equal("user-1", notification.LogicalRecipientId);
        Assert.Equal("gmail", notification.SmtpDestinationId);
        Assert.Equal("recipient@example.test", notification.Destination);
        Assert.Equal("subject", notification.Subject);
        Assert.Equal("body", notification.Body);
        Assert.Equal(NotificationStatus.Pending, notification.Status);
        Assert.Equal(0, notification.AttemptCount);
        Assert.Null(notification.ReservationToken);
        Assert.Equal(now, notification.UpdatedAt);
        Assert.Throws<InvalidOperationException>(() => notification.MarkAsSent(now));
        Assert.Throws<InvalidOperationException>(notification.MarkAsFailed);
        Assert.Throws<InvalidOperationException>(notification.MarkAsPending);
    }

    [Fact]
    public void Construtor_legado_permanece_disponivel_sem_inventar_conteudo()
    {
        var eventId = Guid.NewGuid();
        var notification = new Notification(eventId, Guid.NewGuid(), Guid.NewGuid(),
            NotificationType.Welcome, "recipient@example.test", DateTimeOffset.UtcNow);
        Assert.Equal(eventId.ToString(), notification.BusinessOperationId);
        Assert.Equal("legacy", notification.SmtpDestinationId);
        Assert.Null(notification.Body);
        notification.MarkAsSent(DateTimeOffset.UtcNow);
        Assert.Equal(NotificationStatus.Sent, notification.Status);
        Assert.Throws<InvalidOperationException>(notification.MarkAsPending);
    }
}
