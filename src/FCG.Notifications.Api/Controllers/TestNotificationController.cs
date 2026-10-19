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
        public async Task<IActionResult> TestUserCreated()
        {
            await _publishEndpoint.Publish<UserCreatedEvent>(new {
                Email = "leoloss@uorak.com",
                Name = "Utilizador de Teste"
            });

            return Ok(new { message = "Evento UserCreatedEvent publicado com sucesso!" });
        }

        [HttpPost("payment-processed")]
        public async Task<IActionResult> TestPaymentProcessed()
        {
            await _publishEndpoint.Publish<PaymentProcessedEvent>(new {
                Email = "leoloss@uorak.com",
                Name = "Utilizador de Teste"
            });

            return Ok(new { message = "Evento PaymentProcessedEvent publicado com sucesso!" });
        }
    }
}