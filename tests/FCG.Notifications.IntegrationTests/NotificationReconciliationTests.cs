using System.Data.Common;
using System.Text.Json;
using FCG.Notifications.Application.Abstractions;
using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;
using FCG.Notifications.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using TestDatabase = FCG.Notifications.IntegrationTests.NotificationPersistenceTests.TestDatabase;

namespace FCG.Notifications.IntegrationTests;

public class NotificationReconciliationTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(ReconciliationActionType.ConfirmAcceptance, NotificationStatus.Sent)]
    [InlineData(ReconciliationActionType.RescheduleFailure, NotificationStatus.Pending)]
    [InlineData(ReconciliationActionType.AuthorizeResend, NotificationStatus.Pending)]
    public async Task Transicao_e_auditoria_preservam_entrega_sem_SMTP(ReconciliationActionType action, NotificationStatus expected)
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database, action);
        await using var db = database.Open();
        var safety = new OfflineTestSafety();
        var repository = new NotificationRepository(db, safety);
        var result = await repository.ReconcileAsync(request, Ct);
        var delivery = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        Assert.Equal(expected, delivery.Status);
        Assert.Equal(2, delivery.AttemptCount);
        Assert.Equal("private@example.test", delivery.Destination);
        Assert.Equal("private-body", delivery.Body);
        Assert.Equal("PRIOR_FAILURE", delivery.FailureCode);
        Assert.Null(delivery.NextAttemptAt);
        Assert.Null(delivery.ReservationToken);
        Assert.True(delivery.UpdatedAt > request.ExpectedUpdatedAt);
        Assert.Equal(action == ReconciliationActionType.AuthorizeResend, safety.Released);
        var history = Assert.Single(await repository.GetHistoryAsync(request.NotificationId, 1, 10, Ct));
        Assert.Equal(result.AuditId, history.AuditId);
        Assert.Equal(request.OperatorId, history.OperatorId);
        Assert.Equal(request.ExpectedStatus, history.PreviousStatus);
        Assert.Equal(expected, history.NewStatus);
        Assert.Equal(action, history.ActionType);
        Assert.Empty(await repository.GetHistoryAsync(request.NotificationId, 2, 1, Ct));
        Assert.Empty(await repository.GetHistoryAsync(Guid.NewGuid(), 1, 10, Ct));
        var json = JsonSerializer.Serialize(history);
        foreach (var sensitive in new[] { "private@example.test", "private-body", "private-subject", "ReservationToken", "RequestHash" })
            Assert.DoesNotContain(sensitive, json, StringComparison.Ordinal);
        Assert.Empty(db.ChangeTracker.Entries());
        // No sender/transport is constructed or injected by reconciliation.
    }

    [Fact]
    public async Task ActionId_repetido_retorna_decisao_original_e_parametros_diferentes_conflitam()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        var first = await repository.ReconcileAsync(request, Ct);
        Assert.Equal(first, await repository.ReconcileAsync(request, Ct));
        foreach (var changed in new[] { request with { Reason = "Different reason" }, request with { OperatorId = "another-operator" },
            request with { NotificationId = Guid.NewGuid() }, request with { EvidenceReference = "case-2" },
            request with { ExpectedUpdatedAt = request.ExpectedUpdatedAt.AddSeconds(1) } })
            await Assert.ThrowsAsync<NotificationReconciliationConflictException>(() => repository.ReconcileAsync(changed, Ct));
        Assert.Equal(1, await db.NotificationReconciliationAudits.CountAsync(Ct));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Acoes_concorrentes_tem_uma_decisao_e_uma_auditoria(bool sameAction)
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        async Task<NotificationReconciliationResult?> RunAsync(NotificationReconciliationRequest command)
        {
            await using var db = database.Open();
            try { return await new NotificationRepository(db).ReconcileAsync(command, Ct); }
            catch (NotificationReconciliationConflictException) { return null; }
        }
        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RunAsync(sameAction ? request : request with { ActionId = Guid.NewGuid() })));
        Assert.Equal(sameAction ? 6 : 1, results.Count(r => r is not null));
        Assert.Single(results.Where(r => r is not null).Select(r => r!.AuditId).Distinct());
        await using var verify = database.Open();
        Assert.Equal(1, await verify.NotificationReconciliationAudits.CountAsync(Ct));
    }

    [Fact]
    public async Task Estado_ou_versao_desatualizados_e_Sent_terminal_sao_rejeitados()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        await using var db = database.Open();
        var repository = new NotificationRepository(db);
        await Assert.ThrowsAsync<NotificationReconciliationConflictException>(() => repository.ReconcileAsync(request with { ExpectedUpdatedAt = request.ExpectedUpdatedAt.AddSeconds(-1) }, Ct));
        await repository.ReconcileAsync(request, Ct);
        await Assert.ThrowsAsync<NotificationReconciliationConflictException>(() => repository.ReconcileAsync(request with { ActionId = Guid.NewGuid() }, Ct));
        Assert.Null(await repository.TryAcquireAsync(request.NotificationId, TimeSpan.FromMinutes(5), Ct));
        Assert.False(await repository.TryMarkUnknownAsync(request.NotificationId, Guid.NewGuid(), "OLD_OWNER", Ct));
        Assert.Equal(NotificationStatus.Sent, (await repository.GetAsync(request.NotificationId, Ct))!.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reserva_ativa_ou_expirada_impede_reconciliacao(bool expired)
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        await using var db = database.Open();
        var expires = DateTimeOffset.UtcNow.AddHours(expired ? -1 : 1);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"ReservationToken\" = {Guid.NewGuid()}, \"ReservationExpiresAt\" = {expires} WHERE \"Id\" = {request.NotificationId}", Ct);
        await Assert.ThrowsAsync<NotificationReconciliationConflictException>(() => new NotificationRepository(db).ReconcileAsync(request, Ct));
        Assert.Empty(await db.NotificationReconciliationAudits.ToListAsync(Ct));
    }

    [Fact]
    public async Task Unknown_nao_reenvia_sem_guardiao_confiavel_mesmo_com_declaracao_do_operador()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database, ReconciliationActionType.AuthorizeResend);
        await using var db = database.Open();
        await Assert.ThrowsAsync<InvalidOperationException>(() => new NotificationRepository(db).ReconcileAsync(request, Ct));
        Assert.Equal(NotificationStatus.Unknown, (await db.Notifications.SingleAsync(Ct)).Status);
        Assert.Empty(await db.NotificationReconciliationAudits.ToListAsync(Ct));
    }

    [Fact]
    public async Task Falha_na_auditoria_reverte_transicao_e_permite_repetir_ActionId()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        await using var db = database.Open();
        await db.Database.ExecuteSqlRawAsync("""
            ALTER TABLE notification_reconciliation_audits ADD CONSTRAINT test_reject_audit CHECK ("OperatorId" <> 'operator-1')
            """, Ct);
        await Assert.ThrowsAsync<PostgresException>(() => new NotificationRepository(db).ReconcileAsync(request, Ct));
        Assert.Equal(NotificationStatus.Unknown, (await db.Notifications.AsNoTracking().SingleAsync(Ct)).Status);
        Assert.Empty(await db.NotificationReconciliationAudits.ToListAsync(Ct));
        await db.Database.ExecuteSqlRawAsync("ALTER TABLE notification_reconciliation_audits DROP CONSTRAINT test_reject_audit", Ct);
        await new NotificationRepository(db).ReconcileAsync(request, Ct);
    }

    [Fact]
    public async Task Falha_no_commit_reverte_auditoria_e_entrega_antes_de_liberar_guardiao()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database, ReconciliationActionType.AuthorizeResend);
        await using var source = database.Open();
        var safety = new OfflineTestSafety();
        var interceptor = new FailCommitInterceptor(safety);
        await using var db = new NotificationsDbContext(new DbContextOptionsBuilder<NotificationsDbContext>()
            .UseNpgsql(source.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
        await Assert.ThrowsAsync<IOException>(() => new NotificationRepository(db, safety).ReconcileAsync(request, Ct));
        Assert.True(interceptor.RolledBack);
        Assert.True(safety.Released);
        Assert.Equal(NotificationStatus.Unknown, (await source.Notifications.SingleAsync(Ct)).Status);
        Assert.Empty(await source.NotificationReconciliationAudits.ToListAsync(Ct));
        await new NotificationRepository(source, new OfflineTestSafety()).ReconcileAsync(request, Ct);
    }

    [Theory]
    [InlineData("UPDATE notification_reconciliation_audits SET \"Reason\" = 'changed'")]
    [InlineData("DELETE FROM notification_reconciliation_audits")]
    [InlineData("TRUNCATE notification_reconciliation_audits")]
    public async Task Auditoria_e_imutavel_no_PostgreSQL(string sql)
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database);
        await using var db = database.Open();
        await new NotificationRepository(db).ReconcileAsync(request, Ct);
        await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlRawAsync(sql, Ct));
        Assert.Equal(1, await db.NotificationReconciliationAudits.CountAsync(Ct));
    }

    [Fact]
    public async Task Dispatcher_e_reagendamento_concorrentes_nao_perdem_reserva()
    {
        await using var database = await TestDatabase.CreateAsync();
        var request = await SeedAsync(database, ReconciliationActionType.RescheduleFailure);
        await using (var setup = database.Open())
            await setup.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"NextAttemptAt\" = clock_timestamp() - interval '1 second' WHERE \"Id\" = {request.NotificationId}", Ct);
        async Task ReconcileAsync()
        {
            await using var db = database.Open();
            try { await new NotificationRepository(db).ReconcileAsync(request, Ct); }
            catch (NotificationReconciliationConflictException) { }
        }
        async Task AcquireAsync()
        {
            await using var db = database.Open();
            Assert.NotNull(await new NotificationRepository(db).TryAcquireAsync(request.NotificationId, TimeSpan.FromMinutes(5), Ct));
        }
        await Task.WhenAll(ReconcileAsync(), AcquireAsync());
        await using var verify = database.Open();
        var delivery = await verify.Notifications.SingleAsync(Ct);
        Assert.Equal(NotificationStatus.Preparing, delivery.Status);
        Assert.NotNull(delivery.ReservationToken);
        Assert.Equal(3, delivery.AttemptCount);
    }

    private static async Task<NotificationReconciliationRequest> SeedAsync(TestDatabase database,
        ReconciliationActionType action = ReconciliationActionType.ConfirmAcceptance)
    {
        var notification = new Notification(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), NotificationType.Welcome,
            Guid.NewGuid().ToString(), "logical-private", "route-1", "private@example.test", "private-subject", "private-body", DateTimeOffset.UtcNow);
        await database.InsertAsync(notification);
        var status = action == ReconciliationActionType.RescheduleFailure ? NotificationStatus.Failed : NotificationStatus.Unknown;
        await using var db = database.Open();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE notifications SET \"Status\" = {status.ToString()}, \"AttemptCount\" = 2, \"FailureCode\" = 'PRIOR_FAILURE' WHERE \"Id\" = {notification.Id}", Ct);
        var persisted = await db.Notifications.AsNoTracking().SingleAsync(Ct);
        return new(Guid.NewGuid(), notification.Id, action, status, persisted.UpdatedAt,
            "operator-1", "Investigated operational incident", "case-1",
            DuplicateRiskAcknowledged: action == ReconciliationActionType.AuthorizeResend,
            CauseInvestigated: action == ReconciliationActionType.RescheduleFailure,
            QuiescenceEvidenceReference: action == ReconciliationActionType.AuthorizeResend ? "offline-test-environment" : null);
    }

    // Only valid for these isolated tests: there are no running SMTP dispatcher processes.
    private sealed class OfflineTestSafety : INotificationReplaySafety, IAsyncDisposable
    {
        public bool Released { get; private set; }
        public Task<IAsyncDisposable> AcquireQuiescenceAsync(string evidenceReference, CancellationToken cancellationToken) => Task.FromResult<IAsyncDisposable>(this);
        public ValueTask DisposeAsync() { Released = true; return ValueTask.CompletedTask; }
    }

    private sealed class FailCommitInterceptor(OfflineTestSafety safety) : DbTransactionInterceptor
    {
        public bool RolledBack { get; private set; }
        public override ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData,
            InterceptionResult result, CancellationToken cancellationToken = default) => throw new IOException("Simulated commit failure");
        public override Task TransactionRolledBackAsync(DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
        {
            Assert.False(safety.Released);
            RolledBack = true;
            return Task.CompletedTask;
        }
    }
}
