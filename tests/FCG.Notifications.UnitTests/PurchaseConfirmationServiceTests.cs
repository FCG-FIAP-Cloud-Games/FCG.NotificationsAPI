using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.UnitTests.TestDoubles;

namespace FCG.Notifications.UnitTests;

public class PurchaseConfirmationServiceTests
{
    [Fact]
    public async Task Resolve_destinatario_pelo_UserId_do_evento_de_pagamento()
    {
        var sender = new RecordingNotificationSender();
        var emailRepository = new InMemoryUserEmailRepository();
        var userId = Guid.NewGuid();
        const string recipient = "comprador+purchase-test@example.net";
        await emailRepository.UpsertAsync(userId, recipient, DateTimeOffset.UtcNow, CancellationToken.None);
        var service = new PurchaseConfirmationService(sender, emailRepository);
        var payment = new PaymentProcessedEvent(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            1,
            Guid.NewGuid(),
            Guid.NewGuid(),
            userId,
            Guid.NewGuid(),
            59.90m,
            "BRL",
            PaymentStatus.Approved);

        await service.NotifyAsync(payment, CancellationToken.None);

        Assert.Equal(recipient, sender.LastMessage?.Recipient);
    }
}
