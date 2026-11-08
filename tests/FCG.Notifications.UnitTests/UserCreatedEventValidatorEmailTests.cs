using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;

namespace FCG.Notifications.UnitTests;

public class UserCreatedEventValidatorEmailTests
{
    [Theory]
    [InlineData("abc")]
    [InlineData("sem-arroba.example.test")]
    [InlineData("@example.test")]
    [InlineData("usuario@")]
    [InlineData("dois@@example.test")]
    public void Email_com_formato_invalido_invalida_o_evento(string email)
    {
        Assert.False(UserCreatedEventValidator.IsValid(CreateEvent(email)));
    }

    [Fact]
    public void Email_com_formato_valido_e_aceito()
    {
        Assert.True(UserCreatedEventValidator.IsValid(CreateEvent("usuario@example.test")));
    }

    private static UserCreatedEvent CreateEvent(string email) =>
        new(
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: DateTimeOffset.UtcNow,
            Version: 1,
            UserId: Guid.NewGuid(),
            Name: "Maria",
            Email: email);
}