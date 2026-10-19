using Microsoft.AspNetCore.Mvc;
using FCG.Notifications.Application.Messaging.Contracts;
using MassTransit;

namespace FCG.Notifications.Api.Controllers
{
    [ApiController]
    [Route("api/[controller]")]
    public class TestNotificationController : ControllerBase
    {
        private readonly IPublishEndpoint _publishEndpoint;

        public TestNotificationController(IPublishEndpoint publishEndpoint)
        {
            _publishEndpoint = publishEndpoint;
        }

        [HttpPost("user-created")]
        public async Task<IActionResult> TestUserCreated([FromBody] TestUserCreatedRequest request)
        {
            var now = DateTimeOffset.UtcNow;
            await _publishEndpoint.Publish(new UserCreatedEvent(
                EventId: Guid.NewGuid(),
                CorrelationId: Guid.NewGuid(),
                OccurredAt: now,
                Version: 1,
                UserId: Guid.NewGuid(),
                Name: request.Name,
                Email: request.Email));

            return Ok(new { message = "Evento UserCreatedEvent publicado com sucesso!" });
        }

    }

    public sealed record TestUserCreatedRequest(string Name, string Email);
}
