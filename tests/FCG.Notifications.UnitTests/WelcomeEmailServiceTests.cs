using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.UnitTests.TestDoubles;

namespace FCG.Notifications.UnitTests;

public class WelcomeEmailServiceTests
{
    [Fact]
    public async Task Envia_notificacao_com_dados_do_evento()
    {
        // Arrange
        var sender = new RecordingNotificationSender();
        var service = new WelcomeEmailService(sender);

        var userCreatedEvent = new UserCreatedEvent
        (
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTime.UtcNow,
            1,
            Guid.NewGuid(),
            "Leonardo",
            "leoloss@uorak.com"
        );

        await service.NotifyAsync(userCreatedEvent, TestContext.Current.CancellationToken);

        Assert.NotNull(sender.LastMessage);
        Assert.Equal("leoloss@uorak.com", sender.LastMessage.Recipient);
        Assert.Contains("Leonardo", sender.LastMessage.Body);
    }
}