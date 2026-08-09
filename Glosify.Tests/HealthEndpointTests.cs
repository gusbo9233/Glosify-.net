using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class HealthEndpointTests
{
    [Fact]
    public async Task HealthEndpointAnswersAnonymously()
    {
        // A probe cannot authenticate, and the application has a fallback policy that
        // requires an authenticated user on every endpoint that does not opt out.
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/healthz");

        response.EnsureSuccessStatusCode();
        Assert.Equal("Healthy", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ReadinessEndpointAnswersAnonymouslyAndChecksSql()
    {
        // Make the failure deterministic even when CI exposes its healthy migration-
        // validation database to the process running the test suite.
        using var factory = new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder => builder.UseSetting(
                "ConnectionStrings:DefaultConnection",
                "Server=127.0.0.1,1;Database=unreachable;User Id=sa;Password=NotUsed_1!;Encrypt=False;Connect Timeout=1;"));
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/readyz");

        Assert.Equal(System.Net.HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("Unhealthy", await response.Content.ReadAsStringAsync());
    }
}
