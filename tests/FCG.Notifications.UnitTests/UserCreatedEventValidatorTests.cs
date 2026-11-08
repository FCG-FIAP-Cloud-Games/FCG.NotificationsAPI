using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Application.Notifications;

namespace FCG.Notifications.UnitTests;

public class UserCreatedEventValidatorTests
{
    private static UserCreatedEvent CreateValidEvent() => new
        (
            EventId: Guid.NewGuid(),
            CorrelationId: Guid.NewGuid(),
            OccurredAt: DateTime.UtcNow,
            Version: 1,
            UserId: Guid.NewGuid(),
            Name: "Leonardo",
            Email: "leoloss@uorak.com"
        );

    [Fact]
    public void Evento_valido_passa_na_validacao() =>
        Assert.True(UserCreatedEventValidator.IsValid(CreateValidEvent()));

    [Fact]
    public void EventId_vazio_invalida_o_evento()
    {
        var @event = CreateValidEvent() with { EventId = Guid.Empty };
        Assert.False(UserCreatedEventValidator.IsValid(@event));
    }

    [Fact]
    public void CorrelationId_vazio_invalida_o_evento()
    {
        var @event = CreateValidEvent() with { CorrelationId = Guid.Empty };
        Assert.False(UserCreatedEventValidator.IsValid(@event));
    }

    [Fact]
    public void UserId_vazio_invalida_o_evento()
    {
        var @event = CreateValidEvent() with { UserId = Guid.Empty };
        Assert.False(UserCreatedEventValidator.IsValid(@event));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Nome_nulo_ou_vazio_invalida_o_evento(string? name)
    {
        var @event = CreateValidEvent() with { Name = name! };
        Assert.False(UserCreatedEventValidator.IsValid(@event));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    public void Email_nulo_ou_vazio_invalida_o_evento(string? email)
    {
        var @event = CreateValidEvent() with { Email = email! };
        Assert.False(UserCreatedEventValidator.IsValid(@event));
    }
}