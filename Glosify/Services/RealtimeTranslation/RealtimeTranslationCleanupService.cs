namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<RealtimeTranslationCleanupService> _logger;

    public RealtimeTranslationCleanupService(
        IServiceScopeFactory scopeFactory,
        ILogger<RealtimeTranslationCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var service = scope.ServiceProvider.GetRequiredService<IRealtimeTranslationService>();
                await service.CleanupStaleSessionsAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Realtime translation cleanup failed");
            }
        }
    }
}
