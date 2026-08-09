using Glosify.Data;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Glosify.Infrastructure.Health;

public sealed class DatabaseReadinessHealthCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<DatabaseReadinessHealthCheck> logger) : IHealthCheck
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var database = scope.ServiceProvider.GetRequiredService<GlosifyContext>().Database;
            return await database.CanConnectAsync(timeout.Token)
                ? HealthCheckResult.Healthy("The SQL database is reachable.")
                : HealthCheckResult.Unhealthy("The SQL database is not reachable.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("The SQL readiness check timed out.");
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "SQL readiness check failed");
            return HealthCheckResult.Unhealthy("The SQL database is not reachable.");
        }
    }
}
