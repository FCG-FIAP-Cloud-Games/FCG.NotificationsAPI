using System.Text.Json;
using FCG.Notifications.Application.Messaging.Contracts;

namespace FCG.Notifications.UnitTests;

/// <summary>
/// Confirma que o contrato local desserializa corretamente o payload real
/// publicado pela PaymentsAPI — não apenas um objeto gerado por nós mesmos.
/// </summary>
public class PaymentProcessedEventContractCompatibilityTests
{
    private const string PaymentsApiPayload = """
    {
      "eventId": "b37ae87f-b495-4f36-bae0-7af427d306e8",
      "correlationId": "51dfd30e-9ef1-4f4a-8b8d-622d51352968",
      "occurredAt": "2026-10-05T12:00:00+00:00",
      "version": 1,
      "paymentId": "19daf391-8031-47c7-ad93-7e926a013bf9",
      "orderId": "1c88396e-8b6e-4079-bf39-b880479123f9",
      "userId": "e3992e5d-a92b-46e4-92e4-0094c7f34a6f",
      "gameId": "504935aa-7364-42d6-96e9-40c2ce18a488",
      "amount": 49.99,
      "currency": "BRL",
      "status": "Approved"
    }
    """;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    [Fact]
    public void Desserializa_payload_real_da_PaymentsAPI_corretamente()
    {
        var result = JsonSerializer.Deserialize<PaymentProcessedEvent>(PaymentsApiPayload, Options);

        Assert.NotNull(result);
        Assert.Equal(Guid.Parse("b37ae87f-b495-4f36-bae0-7af427d306e8"), result.EventId);
        Assert.Equal(Guid.Parse("19daf391-8031-47c7-ad93-7e926a013bf9"), result.PaymentId);
        Assert.Equal(Guid.Parse("1c88396e-8b6e-4079-bf39-b880479123f9"), result.OrderId);
        Assert.Equal("BRL", result.Currency);
        Assert.Equal(49.99m, result.Amount);
        Assert.Equal(PaymentStatus.Approved, result.Status);
    }

    [Fact]
    public void Status_rejected_como_string_desserializa_corretamente()
    {
        var payload = PaymentsApiPayload.Replace("\"Approved\"", "\"Rejected\"");

        var result = JsonSerializer.Deserialize<PaymentProcessedEvent>(payload, Options);

        Assert.Equal(PaymentStatus.Rejected, result?.Status);
    }
}