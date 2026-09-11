using Glosify.Data;
using Glosify.Services.Abuse;
using Microsoft.EntityFrameworkCore;
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
            var database = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            if (!await database.Database.CanConnectAsync(timeout.Token))
                return HealthCheckResult.Unhealthy("The SQL database is not reachable.");

            // Exercise representative retained tables so deployment readiness catches
            // a reachable database with an incompatible or partially applied schema.
            _ = await database.Quizzes.AsNoTracking().Select(quiz => quiz.Id).FirstOrDefaultAsync(timeout.Token);
            _ = await database.QuizAttempts.AsNoTracking().Select(attempt => attempt.Id).FirstOrDefaultAsync(timeout.Token);
            _ = await database.BookDocuments.AsNoTracking().Select(book => book.Id).FirstOrDefaultAsync(timeout.Token);

            if (!await database.Set<ResourceAccountingState>().AsNoTracking()
                .AnyAsync(state => state.Id == 1 && state.Ready, timeout.Token))
                return HealthCheckResult.Unhealthy("Resource accounting backfill is not ready.");

            return HealthCheckResult.Healthy("The SQL database, retained schema and resource accounting are ready.");
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
