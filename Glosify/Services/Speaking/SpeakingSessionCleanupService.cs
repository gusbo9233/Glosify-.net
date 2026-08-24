namespace Glosify.Services.Speaking;

/// <summary>
/// Reaps expired speaking sessions across all users.
/// </summary>
/// <remarks>
/// The store only ever pruned as a side effect of the same user starting another session, so a
/// user who closed the tab left their session in memory for the lifetime of the process. Each
/// one pinned a SemaphoreSlim, a session, and locally retained conversation history. The
/// cleanup service bounds that process-local state even when the browser disappears.
/// </remarks>
public sealed class SpeakingSessionCleanupService : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    private readonly ISpeakingSessionStore _sessions;
    private readonly ILogger<SpeakingSessionCleanupService> _logger;

    public SpeakingSessionCleanupService(
        ISpeakingSessionStore sessions,
        ILogger<SpeakingSessionCleanupService> logger)
    {
        _sessions = sessions;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                var removed = await _sessions.RemoveAllExpiredAsync(stoppingToken);
                if (removed > 0)
                {
                    _logger.LogInformation("Reaped {Count} expired speaking sessions", removed);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Speaking session cleanup failed");
            }
        }
    }
}
