using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace FCG.Notifications.IntegrationTests;

// Apenas PostgreSQL descartável: a conexão deve apontar para um banco fcg_notifications_test_*.
public class NotificationPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Inbox_e_intencao_confirmam_juntas_e_rollback_libera_reserva()
    {
        await using var database = await TestDatabase.CreateAsync();
        var eventId = Guid.NewGuid();
        await using (var db = database.Open())
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => new InboxStore(db).TryReserveAsync(eventId, "consumer", Ct));
            await Assert.ThrowsAsync<InvalidOperationException>(() => new UnitOfWork(db).ExecuteInTransactionAsync(async ct =>
            {
                Assert.True(await new InboxStore(db).TryReserveAsync(eventId, "consumer", ct));
                new NotificationRepository(db).Add(CreateNotification(eventId: eventId));
                await db.SaveChangesAsync(ct);
                throw new InvalidOperationException("rollback");
            }, Ct));
        }
        await using (var db = database.Open())
        {
            Assert.Empty(await db.InboxMessages.ToListAsync(Ct));
            Assert.Empty(await db.Notifications.ToListAsync(Ct));
            await new UnitOfWork(db).ExecuteInTransactionAsync(async ct =>
            {
                Assert.True(await new InboxStore(db).TryReserveAsync(eventId, "consumer", ct));
                new NotificationRepository(db).Add(CreateNotification(eventId: eventId));
            }, Ct);
        }
        await using var verify = database.Open();
        Assert.Single(await verify.InboxMessages.ToListAsync(Ct));
        Assert.Single(await verify.Notifications.ToListAsync(Ct));
    }

    [Fact]
    public async Task Reservas_Inbox_concorrentes_tem_um_unico_vencedor()
    {
        await using var database = await TestDatabase.CreateAsync();
        var eventId = Guid.NewGuid();
        async Task<bool> Reserve()
        {
            await using var db = database.Open();
            var won = false;
            await new UnitOfWork(db).ExecuteInTransactionAsync(async ct =>
                won = await new InboxStore(db).TryReserveAsync(eventId, "consumer", ct), Ct);
            return won;
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Reserve()));
        Assert.Equal(1, results.Count(won => won));
    }

    [Fact]
    public async Task Unicidade_de_negocio_independe_do_evento_e_separa_destinos()
    {
        await using var database = await TestDatabase.CreateAsync();
        await using (var db = database.Open())
        {
            db.Notifications.Add(CreateNotification(operation: "payment-1"));
            await db.SaveChangesAsync(Ct);
        }
        await using (var db = database.Open())
        {
            db.Notifications.Add(CreateNotification(operation: "payment-1"));
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Ct));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        }
        await using (var db = database.Open())
        {
            var eventId = Guid.NewGuid();
            db.Notifications.Add(CreateNotification(operation: "payment-1", route: "mailhog", eventId: eventId));
            db.Notifications.Add(CreateNotification(operation: "payment-1", recipient: "user-2", eventId: eventId));
            db.Notifications.Add(CreateNotification(operation: "payment-2", eventId: eventId));
            await db.SaveChangesAsync(Ct);
            Assert.Equal(4, await db.Notifications.CountAsync(Ct));
        }
    }

    [Fact]
    public async Task Aquisicao_concorrente_tem_um_vencedor_e_Sent_e_terminal()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(CreateNotification());
        async Task<Notification?> Acquire()
        {
            await using var db = database.Open();
            return await new NotificationRepository(db).TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct);
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Acquire()));
        var acquired = Assert.Single(results, n => n is not null)!;
        Assert.Equal(1, acquired.AttemptCount);
        Assert.Equal(NotificationStatus.Preparing, acquired.Status);
        Assert.NotNull(acquired.LastAttemptAt);
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var token = acquired.ReservationToken!.Value;
        Assert.False(await repository.TryMarkSentAsync(id, token, Ct));
        Assert.False(await repository.TryStartSendingAsync(id, Guid.NewGuid(), Ct));
        Assert.True(await repository.TryStartSendingAsync(id, token, Ct));
        Assert.False(await repository.TryMarkFailedAsync(id, token, "CONNECT_FAILED", null, Ct));
        Assert.True(await repository.TryMarkSentAsync(id, token, Ct));
        Assert.False(await repository.TryMarkUnknownAsync(id, token, "SEND_UNCERTAIN", Ct));
        Assert.Null(await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));
        var sent = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        Assert.NotNull(sent.SentAt);
        Assert.Null(sent.ReservationToken);
    }

    [Fact]
    public async Task Preparacao_expirada_permite_novo_token_e_rejeita_dono_antigo()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(CreateNotification());
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var old = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
        await ExpireAsync(db, id);
        Assert.False(await repository.TryStartSendingAsync(id, old.ReservationToken!.Value, Ct));
        Assert.Equal(1, await repository.RecoverExpiredAsync(Ct));
        var current = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
        Assert.Equal(2, current.AttemptCount);
        Assert.NotEqual(old.ReservationToken, current.ReservationToken);
        Assert.False(await repository.TryMarkFailedAsync(id, old.ReservationToken.Value, "CONNECT_FAILED", null, Ct));
        Assert.False(await repository.TryStartSendingAsync(id, old.ReservationToken.Value, Ct));
        Assert.True(await repository.TryStartSendingAsync(id, current.ReservationToken!.Value, Ct));
        Assert.False(await repository.TryMarkSentAsync(id, old.ReservationToken.Value, Ct));
        Assert.False(await repository.TryMarkUnknownAsync(id, old.ReservationToken.Value, "SEND_UNCERTAIN", Ct));
    }

    [Fact]
    public async Task Sending_expirado_vira_Unknown_e_nao_pode_ser_readquirido()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(CreateNotification());
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var acquired = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
        Assert.True(await repository.TryStartSendingAsync(id, acquired.ReservationToken!.Value, Ct));
        await ExpireAsync(db, id);
        Assert.False(await repository.TryMarkSentAsync(id, acquired.ReservationToken.Value, Ct));
        Assert.Equal(1, await repository.RecoverExpiredAsync(Ct));
        Assert.Null(await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));
        var unknown = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(NotificationStatus.Unknown, unknown.Status);
        Assert.NotNull(unknown.UnknownAt);
        Assert.Null(unknown.NextAttemptAt);
    }

    [Fact]
    public async Task Falhas_conhecidas_respeitam_agendamento_e_resultado_incerto_nao_reenvia()
    {
        await using var database = await TestDatabase.CreateAsync();
        var id = await database.InsertAsync(CreateNotification());
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var acquired = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
        await Assert.ThrowsAsync<ArgumentException>(() => repository.TryMarkFailedAsync(id, acquired.ReservationToken!.Value, "email@example.test", null, Ct));
        Assert.True(await repository.TryMarkFailedAsync(id, acquired.ReservationToken!.Value, "CONNECT_FAILED", DateTimeOffset.UtcNow.AddHours(1), Ct));
        Assert.Null(await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {id}", Ct);
        var retry = (await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct))!;
        Assert.Equal(2, retry.AttemptCount);
        Assert.True(await repository.TryStartSendingAsync(id, retry.ReservationToken!.Value, Ct));
        Assert.True(await repository.TryMarkUnknownAsync(id, retry.ReservationToken.Value, "SEND_UNCERTAIN", Ct));
        Assert.Null(await repository.TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));

        var terminalId = await database.InsertAsync(CreateNotification(operation: "payment-2"));
        var terminal = (await repository.TryAcquireAsync(terminalId, TimeSpan.FromMinutes(5), Ct))!;
        Assert.True(await repository.TryMarkFailedAsync(terminalId, terminal.ReservationToken!.Value, "INVALID_CONFIGURATION", null, Ct));
        Assert.Null(await repository.TryAcquireAsync(terminalId, TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task Migration_preserva_registros_legados_e_nao_os_despacha()
    {
        await using var database = await TestDatabase.CreateAsync(migrate: false);
        await using var db = database.Open();
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261022133017_InitialNotificationsSchema", Ct);
        var id = Guid.NewGuid();
        var eventId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications ("Id", "EventId", "CorrelationId", "UserId", "Type", "Destination", "Status", "CreatedAt", "SentAt")
            VALUES ({id}, {eventId}, {Guid.NewGuid()}, {userId}, 'Welcome', 'legacy@example.test', 'Sent', clock_timestamp(), clock_timestamp())
            """, Ct);
        var pendingId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO notifications ("Id", "EventId", "CorrelationId", "UserId", "Type", "Destination", "Status", "CreatedAt")
            VALUES ({pendingId}, {Guid.NewGuid()}, {Guid.NewGuid()}, {userId}, 'Welcome', 'legacy@example.test', 'Pending', clock_timestamp())
            """, Ct);
        await migrator.MigrateAsync(cancellationToken: Ct);
        Assert.Equal(2, await db.Notifications.CountAsync(Ct));
        var legacy = await db.Notifications.SingleAsync(n => n.Id == id, Ct);
        Assert.Equal(id, legacy.Id);
        Assert.Equal(eventId.ToString(), legacy.BusinessOperationId);
        Assert.Equal(userId.ToString(), legacy.LogicalRecipientId);
        Assert.Equal("legacy@example.test", legacy.Destination);
        Assert.Equal(NotificationStatus.Sent, legacy.Status);
        Assert.Equal(legacy.SentAt, legacy.UpdatedAt);
        Assert.Null(legacy.Subject);
        Assert.Null(await new NotificationRepository(db).TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));
        Assert.Null(await new NotificationRepository(db).TryAcquireAsync(pendingId, TimeSpan.FromMinutes(5), Ct));
    }

    [Fact]
    public async Task Entidade_rastreada_obsoleta_nao_sobrescreve_reserva()
    {
        await using var database = await TestDatabase.CreateAsync();
        var legacy = new Notification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationType.Welcome,
            "legacy@example.test", DateTimeOffset.UtcNow);
        var id = await database.InsertAsync(legacy);
        await using var stale = database.Open();
        var tracked = await stale.Notifications.SingleAsync(Ct);
        await using var current = database.Open();
        await current.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"Subject\" = 'subject', \"Body\" = 'body' WHERE \"Id\" = {id}", Ct);
        Assert.NotNull(await new NotificationRepository(current).TryAcquireAsync(id, TimeSpan.FromMinutes(5), Ct));
        tracked.MarkAsSent(DateTimeOffset.UtcNow);
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync(Ct));
    }

    private static Notification CreateNotification(string operation = "payment-1", string route = "gmail",
        string recipient = "user-1", Guid? eventId = null) =>
        new(eventId ?? Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationType.PurchaseConfirmation,
            operation, recipient, route, "recipient@example.test", "subject", "body", DateTimeOffset.UtcNow);

    private static Task<int> ExpireAsync(NotificationsDbContext db, Guid id) =>
        db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"ReservationExpiresAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {id}", Ct);

    internal sealed class TestDatabase(string connectionString, string schema) : IAsyncDisposable
    {
        public NotificationsDbContext Open() => new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);

        public static async Task<TestDatabase> CreateAsync(bool migrate = true)
        {
            var connection = Environment.GetEnvironmentVariable("FCG_NOTIFICATIONS_TEST_POSTGRES");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Defina FCG_NOTIFICATIONS_TEST_POSTGRES para PostgreSQL descartável.");
            var builder = new NpgsqlConnectionStringBuilder(connection);
            if (builder.Database is null || !builder.Database.StartsWith("fcg_notifications_test_", StringComparison.Ordinal))
                throw new InvalidOperationException("Testes exigem banco descartável fcg_notifications_test_*.");
            var schema = "test_" + Guid.NewGuid().ToString("N");
            await using var admin = new NpgsqlConnection(connection);
            await admin.OpenAsync(Ct);
            await using var command = new NpgsqlCommand($"CREATE SCHEMA {schema}", admin);
            await command.ExecuteNonQueryAsync(Ct);
            builder.SearchPath = schema;
            var database = new TestDatabase(builder.ConnectionString, schema);
            if (migrate)
            {
                await using var db = database.Open();
                await db.Database.MigrateAsync(Ct);
            }
            return database;
        }

        public async Task<Guid> InsertAsync(Notification notification)
        {
            await using var db = Open();
            db.Notifications.Add(notification);
            await db.SaveChangesAsync(Ct);
            return notification.Id;
        }

        public async ValueTask DisposeAsync()
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", connection);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
