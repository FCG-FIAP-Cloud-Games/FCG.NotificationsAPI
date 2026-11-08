using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.UnitTests;

public class NotificationOperationsFilterTests
{
    [Theory]
    [InlineData(0, 50)]
    [InlineData(-1, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    [InlineData(int.MaxValue, 100)]
    public void Rejeita_paginacao_invalida(int page, int pageSize) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationOperationsFilter(Page: page, PageSize: pageSize).Validate());

    [Fact]
    public void Rejeita_filtros_invalidos()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new NotificationOperationsFilter(Status: (NotificationStatus)99).Validate());
        var date = DateTimeOffset.UtcNow;
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(CreatedFrom: date, CreatedUntil: date).Validate());
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(CreatedFrom: date, CreatedUntil: date.AddDays(-1)).Validate());
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(BusinessOperationId: " ").Validate());
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(SmtpDestinationId: "").Validate());
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(BusinessOperationId: new string('a', 201)).Validate());
        Assert.Throws<ArgumentException>(() => new NotificationOperationsFilter(SmtpDestinationId: new string('a', 101)).Validate());
    }
}
