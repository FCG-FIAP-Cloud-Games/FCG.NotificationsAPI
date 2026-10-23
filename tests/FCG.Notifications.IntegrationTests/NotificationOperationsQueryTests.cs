using System.Text.Json;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class NotificationOperationsQueryTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(NotificationStatus.Pending)]
    [InlineData(NotificationStatus.Preparing)]
    [InlineData(NotificationStatus.Sending)]
    [InlineData(NotificationStatus.Sent)]
    [InlineData(NotificationStatus.Failed)]
    [InlineData(NotificationStatus.Unknown)]
    public async Task Filtra_todos_os_estados(NotificationStatus status)
    {
        await using var database = await TestDatabase.CreateAsync();
        await using var db = database.Open();
        foreach (var value in Enum.GetValues<NotificationStatus>())
        {
            var id = await database.InsertAsync(Delivery(value.ToString()));
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"Status\" = {value.ToString()} WHERE \"Id\" = {id}", Ct);
        }
        var result = await new NotificationRepository(db).SearchAsync(new(Status: status), Ct);
        Assert.Equal(status, Assert.Single(result.Items).Status);
        Assert.False(result.HasNextPage);
    }

    [Fact]
    public async Task Filtros_exatos_e_periodo_inclusivo_exclusivo_combinam_no_banco()
    {
        await using var database = await TestDatabase.CreateAsync();
        var expected = await database.InsertAsync(Delivery("payment-1", "route-a", Start));
        await database.InsertAsync(Delivery("payment-1", "route-b", Start));
        await database.InsertAsync(Delivery("payment-2", "route-a", Start.AddHours(1)));
        await database.InsertAsync(Delivery("payment-3", "route-a", Start.AddSeconds(-1)));
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        Assert.Equal(2, (await repository.SearchAsync(new(BusinessOperationId: "payment-1"), Ct)).Items.Count);
        Assert.Equal(3, (await repository.SearchAsync(new(SmtpDestinationId: "route-a"), Ct)).Items.Count);
        var from = Start.ToOffset(TimeSpan.FromHours(-3));
        var until = Start.AddHours(1).ToOffset(TimeSpan.FromHours(-3));
        Assert.Equal(2, (await repository.SearchAsync(new(CreatedFrom: from, CreatedUntil: until), Ct)).Items.Count);
        var combined = await repository.SearchAsync(new(NotificationStatus.Pending, from, until, "payment-1", "route-a"), Ct);
        Assert.Equal(expected, Assert.Single(combined.Items).NotificationId);
        Assert.Empty((await repository.SearchAsync(new(BusinessOperationId: "payment"), Ct)).Items);
        Assert.Empty((await repository.SearchAsync(new(SmtpDestinationId: "route"), Ct)).Items);
    }

    [Fact]
    public async Task Paginacao_ordena_por_data_e_desempata_por_Id_sem_repetir_registros()
    {
        await using var database = await TestDatabase.CreateAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await database.InsertAsync(Delivery($"operation-{i}")));
        var latest = await database.InsertAsync(Delivery("latest", createdAt: Start.AddDays(1)));
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var actual = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var result = await repository.SearchAsync(new(Page: page, PageSize: 2), Ct);
            Assert.Equal(page, result.Page);
            Assert.Equal(2, result.PageSize);
            Assert.Equal(page < 3, result.HasNextPage);
            actual.AddRange(result.Items.Select(n => n.NotificationId));
            Assert.Equal(result.Items, (await repository.SearchAsync(new(Page: page, PageSize: 2), Ct)).Items);
        }
        Assert.Equal(new[] { latest }.Concat(ids.OrderDescending()), actual);
        var empty = await repository.SearchAsync(new(Page: 4, PageSize: 2), Ct);
        Assert.Empty(empty.Items);
        Assert.False(empty.HasNextPage);
    }

    [Fact]
    public async Task Detalhe_e_lista_sao_projecoes_sem_segredos_tracking_ou_escritas()
    {
        await using var database = await TestDatabase.CreateAsync();
        var notification = Delivery("safe-operation");
        await database.InsertAsync(notification);
        await using var source = database.Open();
        var commands = new List<string>();
        await using var db = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(source.Database.GetConnectionString())
            .LogTo(commands.Add, [RelationalEventId.CommandExecuted]).Options);
        await using var transaction = await db.Database.BeginTransactionAsync(Ct);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", Ct);
        commands.Clear();
        var repository = new NotificationRepository(db);
        var detail = await repository.GetAsync(notification.Id, Ct);
        Assert.NotNull(detail);
        Assert.Equal(notification.EventId, detail.EventId);
        Assert.Equal(notification.CorrelationId, detail.CorrelationId);
        Assert.Equal(notification.CreatedAt, detail.CreatedAt);
        Assert.Equal(detail, Assert.Single((await repository.SearchAsync(new(), Ct)).Items));
        Assert.Null(await repository.GetAsync(Guid.NewGuid(), Ct));
        Assert.Empty(db.ChangeTracker.Entries());
        var serialized = JsonSerializer.Serialize(detail);
        foreach (var field in new[] { "Destination", "Subject", "Body", "UserId", "LogicalRecipientId", "ReservationToken" })
        {
            Assert.DoesNotContain($"\"{field}\"", serialized, StringComparison.Ordinal);
            Assert.All(commands, sql => Assert.DoesNotContain($"\"{field}\"", sql, StringComparison.Ordinal));
        }
        Assert.DoesNotContain("private@example.test", serialized, StringComparison.Ordinal);
        Assert.DoesNotContain("private-content", serialized, StringComparison.Ordinal);
        Assert.Equal(3, commands.Count);
        Assert.All(commands, sql => Assert.Contains("SELECT", sql, StringComparison.Ordinal));
        await transaction.CommitAsync(Ct);
    }

    private static Notification Delivery(string operation, string route = "route-a", DateTimeOffset? createdAt = null) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationType.Welcome, operation, "logical-private",
            route, "private@example.test", "private-subject", "private-content", createdAt ?? Start);
}
