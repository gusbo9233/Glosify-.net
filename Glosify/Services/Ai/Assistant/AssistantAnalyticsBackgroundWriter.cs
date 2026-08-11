using System.Threading.Channels;
using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal interface IAssistantAnalyticsBatchWriter
{
    ValueTask SubmitAsync(
        IReadOnlyCollection<AssistantModelInvocation> invocations,
        IReadOnlyCollection<AssistantToolExecution> executions,
        CancellationToken cancellationToken);
}

internal sealed record AssistantAnalyticsBatch(
    IReadOnlyList<AssistantModelInvocation> Invocations,
    IReadOnlyList<AssistantToolExecution> Executions);

internal sealed class AssistantAnalyticsBackgroundWriter : BackgroundService, IAssistantAnalyticsBatchWriter
{
    private const int QueueCapacity = 128;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AssistantAnalyticsBackgroundWriter> _logger;
    private readonly Channel<AssistantAnalyticsBatch> _queue = Channel.CreateBounded<AssistantAnalyticsBatch>(
        new BoundedChannelOptions(QueueCapacity)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });

    public AssistantAnalyticsBackgroundWriter(
        IServiceScopeFactory scopeFactory,
        ILogger<AssistantAnalyticsBackgroundWriter> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public ValueTask SubmitAsync(
        IReadOnlyCollection<AssistantModelInvocation> invocations,
        IReadOnlyCollection<AssistantToolExecution> executions,
        CancellationToken cancellationToken)
    {
        if (invocations.Count == 0 && executions.Count == 0)
        {
            return ValueTask.CompletedTask;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var batch = new AssistantAnalyticsBatch(invocations.ToArray(), executions.ToArray());
        if (!_queue.Writer.TryWrite(batch))
        {
            _logger.LogWarning(
                "Dropped assistant analytics batch because the bounded queue is full. " +
                "Invocations: {InvocationCount}; tools: {ToolCount}.",
                batch.Invocations.Count,
                batch.Executions.Count);
        }

        return ValueTask.CompletedTask;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var batch in _queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var contextFactory = scope.ServiceProvider
                    .GetRequiredService<IDbContextFactory<GlosifyContext>>();
                await using var context = await contextFactory.CreateDbContextAsync(stoppingToken);
                context.AssistantModelInvocations.AddRange(batch.Invocations);
                context.AssistantToolExecutions.AddRange(batch.Executions);
                await context.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Could not persist assistant analytics batch. Invocations: {InvocationCount}; tools: {ToolCount}.",
                    batch.Invocations.Count,
                    batch.Executions.Count);
            }
        }
    }
}
