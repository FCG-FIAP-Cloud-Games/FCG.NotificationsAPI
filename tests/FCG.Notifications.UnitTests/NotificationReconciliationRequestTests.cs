using FCG.Notifications.Application.Notifications;
using FCG.Notifications.Domain.Notifications.Entities;
using FCG.Notifications.Domain.Notifications.Enums;

namespace FCG.Notifications.UnitTests;

public class NotificationReconciliationRequestTests
{
    [Fact]
    public void Confirmacao_exige_operador_motivo_e_evidencia()
    {
        var request = Valid();
        request.Validate();
        foreach (var invalid in new[] { request with { OperatorId = "" }, request with { Reason = "" },
            request with { EvidenceReference = null }, request with { ActionId = Guid.Empty },
            request with { ExpectedUpdatedAt = default }, request with { Reason = "reason\nsecret" } })
            Assert.Throws<ArgumentException>(invalid.Validate);
    }

    [Fact]
    public void Reenvio_exige_risco_explicito_e_referencia_de_parada()
    {
        var request = Valid() with { ActionType = ReconciliationActionType.AuthorizeResend };
        Assert.Throws<ArgumentException>(request.Validate);
        request = request with { DuplicateRiskAcknowledged = true };
        Assert.Throws<ArgumentException>(request.Validate);
        (request with { QuiescenceEvidenceReference = "maintenance-1" }).Validate();
    }

    [Fact]
    public void Reagendamento_exige_investigacao_e_estado_Failed()
    {
        var request = Valid() with { ActionType = ReconciliationActionType.RescheduleFailure, ExpectedStatus = NotificationStatus.Failed };
        Assert.Throws<ArgumentException>(request.Validate);
        (request with { CauseInvestigated = true }).Validate();
        Assert.Throws<ArgumentException>((request with { CauseInvestigated = true, ExpectedStatus = NotificationStatus.Sent }).Validate);
    }

    private static NotificationReconciliationRequest Valid() => new(Guid.NewGuid(), Guid.NewGuid(),
        ReconciliationActionType.ConfirmAcceptance, NotificationStatus.Unknown, DateTimeOffset.UtcNow,
        "operator-1", "Investigated", "case-1");
}
