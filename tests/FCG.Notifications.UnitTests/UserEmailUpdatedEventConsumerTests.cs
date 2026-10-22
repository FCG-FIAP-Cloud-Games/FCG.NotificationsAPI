using System.Reflection;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Messaging.Contracts;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Infrastructure.Messaging;
using FCG.Notifications.Infrastructure.Messaging.Consumers;
using MassTransit;
using Microsoft.Extensions.Logging.Abstractions;

namespace FCG.Notifications.UnitTests;

public class UserEmailUpdatedEventConsumerTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reserva_na_transacao_e_atualiza_apenas_o_primeiro_processamento(bool reserve)
    {
        var persistence = new RecordingPersistence { Reserve = reserve };
        var message = Message();
        await Consumer(persistence).Consume(ContextProxy.Create(message));
        Assert.Equal(reserve ? "begin,reserve,update,commit" : "begin,reserve,commit", string.Join(',', persistence.Calls));
        Assert.Equal(message.EventId, persistence.EventId);
        Assert.Equal(nameof(UserEmailUpdatedEventConsumer), persistence.ConsumerName);
        if (reserve)
        {
            Assert.Equal(message.Email, persistence.Email);
            Assert.Equal(message.OccurredAt, persistence.OccurredAt);
            Assert.Equal(message.UserId, persistence.UserId);
        }
    }

    [Fact]
    public async Task Evento_invalido_nao_inicia_transacao()
    {
        var persistence = new RecordingPersistence();
        await Assert.ThrowsAsync<PermanentMessageException>(() => Consumer(persistence)
            .Consume(ContextProxy.Create(Message() with { EventId = Guid.Empty })));
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public async Task Falha_de_atualizacao_permanece_observavel()
    {
        var failure = new InvalidOperationException("database failure");
        var persistence = new RecordingPersistence { Failure = failure };
        var actual = await Assert.ThrowsAsync<InvalidOperationException>(() => Consumer(persistence).Consume(ContextProxy.Create(Message())));
        Assert.Same(failure, actual);
        Assert.Equal("begin,reserve,update,rollback", string.Join(',', persistence.Calls));
    }

    private static UserEmailUpdatedEvent Message() => new(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow,
        1, Guid.NewGuid(), "new@example.test");

    private static UserEmailUpdatedEventConsumer Consumer(RecordingPersistence persistence) =>
        new(persistence, persistence, persistence, NullLogger<UserEmailUpdatedEventConsumer>.Instance);

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
            "get_CancellationToken" => TestContext.Current.CancellationToken,
            _ => throw new NotSupportedException(targetMethod?.Name)
        };
    }

    private sealed class RecordingPersistence : IUserEmailRepository, IInboxStore, IUnitOfWork
    {
        public bool Reserve { get; init; } = true;
        public Exception? Failure { get; init; }
        public List<string> Calls { get; } = [];
        public Guid EventId { get; private set; }
        public Guid UserId { get; private set; }
        public string? ConsumerName { get; private set; }
        public string? Email { get; private set; }
        public DateTimeOffset OccurredAt { get; private set; }
        private bool _inTransaction;

        public async Task ExecuteInTransactionAsync(Func<CancellationToken, Task> action, CancellationToken cancellationToken)
        {
            Calls.Add("begin");
            _inTransaction = true;
            try { await action(cancellationToken); Calls.Add("commit"); }
            catch { Calls.Add("rollback"); throw; }
            finally { _inTransaction = false; }
        }
        public Task<bool> TryReserveAsync(Guid eventId, string consumerName, CancellationToken cancellationToken)
        {
            Assert.True(_inTransaction);
            Calls.Add("reserve"); EventId = eventId; ConsumerName = consumerName;
            return Task.FromResult(Reserve);
        }
        public Task UpsertAsync(Guid userId, string email, DateTimeOffset updatedAt, CancellationToken cancellationToken)
        {
            Assert.True(_inTransaction);
            Calls.Add("update"); UserId = userId; Email = email; OccurredAt = updatedAt;
            return Failure is null ? Task.CompletedTask : Task.FromException(Failure);
        }
        public Task<string?> GetEmailAsync(Guid userId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ExistsAsync(Guid eventId, string consumerName, CancellationToken cancellationToken) => throw new NotSupportedException();
        public void Add(InboxMessage message) => throw new NotSupportedException();
        public Task SaveChangesAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
