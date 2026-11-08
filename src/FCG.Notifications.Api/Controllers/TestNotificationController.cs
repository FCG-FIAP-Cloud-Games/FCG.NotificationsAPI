using System.Net.Mail;
using FCG.Notifications.Application.Messaging.Contracts;
using MassTransit;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Notifications.Api.Controllers;

/// <summary>
/// Publica um UserCreatedEvent de teste. Só responde com
/// <c>Messaging:EnableTestEndpoints=true</c> (variável <c>Messaging__EnableTestEndpoints</c>);
/// caso contrário devolve 404, como se a rota não existisse.
/// </summary>
[ApiController]
[Route("api/[controller]")]
public sealed class TestNotificationController(
    IPublishEndpoint publishEndpoint,
    IConfiguration configuration) : ControllerBase
{
    [HttpPost("user-created")]
    public async Task<IActionResult> TestUserCreated(
        [FromBody] TestUserCreatedRequest request,
        CancellationToken cancellationToken)
    {
        if (!configuration.GetValue<bool>("Messaging:EnableTestEndpoints"))
        {
            return NotFound();
        }

        if (string.IsNullOrWhiteSpace(request.Name)
            || string.IsNullOrWhiteSpace(request.Email)
            || !MailAddress.TryCreate(request.Email, out _))
        {
            return BadRequest(new { message = "Informe um nome e um e-mail válidos." });
        }

        var eventId = Guid.NewGuid();
        var correlationId = Guid.NewGuid();
        var userId = Guid.NewGuid();

        await publishEndpoint.Publish(
            new UserCreatedEvent(
                EventId: eventId,
                CorrelationId: correlationId,
                OccurredAt: DateTimeOffset.UtcNow,
                Version: 1,
                UserId: userId,
                Name: request.Name,
                Email: request.Email),
            cancellationToken);

        return Ok(new
        {
            message = "Evento UserCreatedEvent publicado. O envio do e-mail acontece no consumer; confira os logs ou o Mailpit/MailHog.",
            EventId = eventId,
            CorrelationId = correlationId,
            UserId = userId
        });
    }
}

public sealed record TestUserCreatedRequest(string Name, string Email);