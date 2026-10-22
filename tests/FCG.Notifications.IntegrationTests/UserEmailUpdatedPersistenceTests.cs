using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Infrastructure.Data;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace FCG.Notifications.IntegrationTests;

public class UserEmailUpdatedPersistenceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset EventTime = new(2026, 10, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Primeiro_processamento_confirma_contato_e_Inbox()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await ProcessAsync(database, message);
        await using var db = database.Open();
        var contact = await db.UserEmailContacts.SingleAsync(Ct);
        var inbox = await db.InboxMessages.SingleAsync(Ct);
        Assert.Equal(message.UserId, contact.UserId);
        Assert.Equal(message.Email, contact.Email);
        Assert.Equal(message.OccurredAt, contact.UpdatedAt);
        Assert.Equal(message.EventId, inbox.EventId);
        Assert.Equal(nameof(UserEmailUpdatedEventConsumer), inbox.ConsumerName);
    }

    [Fact]
    public async Task Reentrega_nao_executa_novamente_atualizacao()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        var updates = 0;
        await ProcessAsync(database, message, onWrite: () => updates++);
        await ProcessAsync(database, message, onWrite: () => updates++);
        Assert.Equal(1, updates);
        await using var db = database.Open();
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
        Assert.Equal(1, await db.UserEmailContacts.CountAsync(Ct));
    }

    [Fact]
    public async Task Consumidores_concorrentes_do_mesmo_evento_aplicam_uma_atualizacao()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        var updates = 0;
        await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => ProcessAsync(database, message,
            onWrite: () => Interlocked.Increment(ref updates))));
        Assert.Equal(1, updates);
        await using var db = database.Open();
        Assert.Equal(1, await db.InboxMessages.CountAsync(Ct));
        Assert.Equal(message.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Evento_mais_recente_vence_em_ambas_as_ordens(bool newestFirst)
    {
        await using var database = await TestDatabase.CreateAsync();
        var older = Message() with { Email = "old@example.test" };
        var newer = older with { EventId = Guid.NewGuid(), Email = "new@example.test", OccurredAt = EventTime.AddMinutes(1).ToOffset(TimeSpan.FromHours(-3)) };
        await ProcessAsync(database, newestFirst ? newer : older);
        await ProcessAsync(database, newestFirst ? older : newer);
        await using var db = database.Open();
        var contact = await db.UserEmailContacts.SingleAsync(Ct);
        Assert.Equal(newer.Email, contact.Email);
        Assert.Equal(newer.OccurredAt, contact.UpdatedAt);
        Assert.Equal(2, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Eventos_distintos_concorrentes_preservam_o_timestamp_mais_recente()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ProcessAsync(database, message with
        {
            EventId = Guid.NewGuid(), Email = $"user{i}@example.test", OccurredAt = EventTime.AddMinutes(i)
        })));
        await using var db = database.Open();
        var contact = await db.UserEmailContacts.SingleAsync(Ct);
        Assert.Equal("user7@example.test", contact.Email);
        Assert.Equal(EventTime.AddMinutes(7), contact.UpdatedAt);
        Assert.Equal(8, await db.InboxMessages.CountAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Empate_divergente_tem_resultado_deterministico(bool largerFirst)
    {
        await using var database = await TestDatabase.CreateAsync();
        var smaller = Message() with { Email = "a@example.test" };
        var larger = smaller with { EventId = Guid.NewGuid(), Email = "z@example.test" };
        await ProcessAsync(database, largerFirst ? larger : smaller);
        await ProcessAsync(database, largerFirst ? smaller : larger);
        await using var db = database.Open();
        Assert.Equal(larger.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
        Assert.Equal(2, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Empates_concorrentes_convergem_e_eventos_identicos_nao_regridem()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => ProcessAsync(database,
            message with { EventId = Guid.NewGuid(), Email = $"user{i}@example.test" })));
        await ProcessAsync(database, message with { EventId = Guid.NewGuid(), Email = "user7@example.test" });
        await using var db = database.Open();
        Assert.Equal("user7@example.test", (await db.UserEmailContacts.SingleAsync(Ct)).Email);
        Assert.Equal(9, await db.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Falha_apos_escrita_reverte_Inbox_e_contato_e_permite_retry()
    {
        await using var database = await TestDatabase.CreateAsync();
        var older = Message();
        await ProcessAsync(database, older);
        var newer = older with { EventId = Guid.NewGuid(), Email = "newer@example.test", OccurredAt = EventTime.AddMinutes(1) };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ProcessAsync(database, newer, failAfterWrite: true));
        await using (var db = database.Open())
        {
            Assert.False(await new InboxStore(db).ExistsAsync(newer.EventId, nameof(UserEmailUpdatedEventConsumer), Ct));
            Assert.Equal(older.Email, (await db.UserEmailContacts.SingleAsync(Ct)).Email);
        }
        await ProcessAsync(database, newer);
        await using var verify = database.Open();
        Assert.Equal(newer.Email, (await verify.UserEmailContacts.SingleAsync(Ct)).Email);
        Assert.Equal(2, await verify.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Falha_PostgreSQL_reverte_reserva_sem_ocultar_excecao()
    {
        await using var database = await TestDatabase.CreateAsync();
        var message = Message();
        await using (var db = database.Open())
            await db.Database.ExecuteSqlRawAsync("ALTER TABLE user_email_contacts ADD CONSTRAINT fail_email_write CHECK (false)", Ct);
        var error = await Assert.ThrowsAsync<PostgresException>(() => ProcessAsync(database, message));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        await using var verify = database.Open();
        Assert.Empty(await verify.InboxMessages.ToListAsync(Ct));
        Assert.Empty(await verify.UserEmailContacts.ToListAsync(Ct));
        await verify.Database.ExecuteSqlRawAsync("ALTER TABLE user_email_contacts DROP CONSTRAINT fail_email_write", Ct);
        await ProcessAsync(database, message);
        Assert.Equal(1, await verify.InboxMessages.CountAsync(Ct));
    }

    [Fact]
    public async Task Repositorio_continua_funcionando_sem_transacao_explicita_para_outros_consumidores()
    {
        await using var database = await TestDatabase.CreateAsync();
        var userId = Guid.NewGuid();
        await using (var db = database.Open())
            await new UserEmailRepository(db).UpsertAsync(userId, " original@example.test ", EventTime, Ct);
        await using var verify = database.Open();
        Assert.Equal("original@example.test", await new UserEmailRepository(verify).GetEmailAsync(userId, Ct));
    }

    private static UserEmailUpdatedEvent Message() => new(Guid.NewGuid(), Guid.NewGuid(), EventTime, 1,
        Guid.NewGuid(), "original@example.test");

    private static async Task ProcessAsync(TestDatabase database, UserEmailUpdatedEvent message,
        bool failAfterWrite = false, Action? onWrite = null)
    {
        await using var db = database.Open();
        var repository = new ObservedRepository(new UserEmailRepository(db), failAfterWrite, onWrite);
        var consumer = new UserEmailUpdatedEventConsumer(repository, new InboxStore(db), new UnitOfWork(db),
            NullLogger<UserEmailUpdatedEventConsumer>.Instance);
        await consumer.Consume(ContextProxy.Create(message));
    }

    private sealed class ObservedRepository(IUserEmailRepository inner, bool failAfterWrite, Action? onWrite) : IUserEmailRepository
    {
        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) => inner.GetEmailAsync(userId, cancellationToken);
        public async Task UpsertAsync(Guid userId, string email, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            onWrite?.Invoke();
            await inner.UpsertAsync(userId, email, updatedAt, cancellationToken);
            if (failAfterWrite) throw new InvalidOperationException("Simulated failure after SQL write.");
        }
    }

    public class ContextProxy : DispatchProxy
    {
        private UserEmailUpdatedEvent? _message;
        public static ConsumeContext<UserEmailUpdatedEvent> Create(UserEmailUpdatedEvent message)
        {
            var context = Create<ConsumeContext<UserEmailUpdatedEvent>, ContextProxy>();
            ((ContextProxy)context)._message = message;
            return context;
        }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => targetMethod?.Name switch
        {
            "get_Message" => _message,
            "get_CancellationToken" => Ct,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private sealed class TestDatabase(string connectionString, string schema) : IAsyncDisposable
    {
        public NotificationsDbContext Open() => new(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(connectionString, options => options.MigrationsHistoryTable("__EFMigrationsHistory", schema)).Options);
        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = Environment.GetEnvironmentVariable("FCG_NOTIFICATIONS_TEST_POSTGRES");
            Assert.SkipWhen(string.IsNullOrWhiteSpace(connection), "Defina PostgreSQL descartável em FCG_NOTIFICATIONS_TEST_POSTGRES.");
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
            await using var db = database.Open();
            await db.Database.MigrateAsync(Ct);
            return database;
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
