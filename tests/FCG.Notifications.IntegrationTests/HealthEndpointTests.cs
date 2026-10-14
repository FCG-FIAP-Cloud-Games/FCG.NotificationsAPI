using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FCG.Notifications.IntegrationTests;

public class HealthEndpointTests(WebApplicationFactory<Program> factory) 
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public async Task Get_health_retorna_200()
    {
        var client = factory.CreateClient();

        var response = await client.GetAsync("/health", TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }
}

