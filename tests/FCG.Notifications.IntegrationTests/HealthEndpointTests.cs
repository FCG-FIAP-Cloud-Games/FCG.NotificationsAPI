using System.Net;
using MassTransit;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Logging;

namespace FCG.Notifications.IntegrationTests;

public class HealthEndpointTests
{
    [Fact]
    public async Task Get_health_retorna_200()
    {
        // Await disposal inside the test so host shutdown failures fail this test too.
        await using var factory = new HealthApplicationFactory();
        using var client = factory.CreateClient();

        using var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private sealed class HealthApplicationFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");
            builder.UseSetting("NotificationsDispatcher:Enabled", "false");
            builder.ConfigureLogging(logging =>
            {
                // EventLog is a Windows host default, not a dependency of /health.
                logging.ClearProviders();
                logging.AddConsole();
            });
            // Exercise MassTransit startup/shutdown without contacting a real broker.
            builder.ConfigureTestServices(services => services.AddMassTransitTestHarness());
        }
    }
}

