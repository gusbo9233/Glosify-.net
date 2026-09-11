using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Glosify.Data;
using Glosify.Services.Abuse;
using Glosify.Infrastructure.Health;

namespace Glosify.Tests;

public sealed class HealthEndpointTests
{
    [SqlServerFact]
    public Task ReadinessRequiresCompletedResourceBackfill() => SqlServerTestDatabase.RunAsync("quota_readiness", async database =>
    {
        var services = new ServiceCollection();
        services.AddDbContext<GlosifyContext>(options => options.UseSqlServer(database.Database.GetConnectionString()));
        await using var provider = services.BuildServiceProvider();
        var check = new DatabaseReadinessHealthCheck(
            provider.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<DatabaseReadinessHealthCheck>.Instance);
        var context = new HealthCheckContext();
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(context)).Status);
        var state = new ResourceAccountingState { Id = 1, Ready = false };
        database.Add(state);
        await database.SaveChangesAsync();
        Assert.Equal(HealthStatus.Unhealthy, (await check.CheckHealthAsync(context)).Status);
        state.Ready = true;
        await database.SaveChangesAsync();
        Assert.Equal(HealthStatus.Healthy, (await check.CheckHealthAsync(context)).Status);
    });

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

    [Fact]
    public async Task DeploymentVersionAnswersAnonymouslyAndIsNotCached()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        var response = await client.GetAsync("/deployment-version");

        response.EnsureSuccessStatusCode();
        Assert.Equal("local", await response.Content.ReadAsStringAsync());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }
}
