using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class AiEnrichmentBackgroundService : BackgroundService
{
    private readonly IAiEnrichmentQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiEnrichmentBackgroundService> _logger;

    public AiEnrichmentBackgroundService(
        IAiEnrichmentQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<AiEnrichmentBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var job in _queue.ReadAllAsync(stoppingToken))
        {
            try
            {
                await ProcessAsync(job, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Swallow: enrichment is best-effort; the on-demand fallback in
                // WordDetailsController.Details will ask Gemini again on first view.
                _logger.LogWarning(ex, "AI word detail enrichment failed for WordDetail {WordDetailId} ({Lemma})",
                    job.WordDetailId, job.Lemma);
            }
        }
    }

    private async Task ProcessAsync(AiEnrichmentJob job, CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var enrichment = scope.ServiceProvider.GetRequiredService<IWordDetailEnrichmentService>();
        var context = scope.ServiceProvider.GetRequiredService<GlosifyContext>();

        var detail = await context.WordDetails.FirstOrDefaultAsync(d => d.Id == job.WordDetailId, cancellationToken);
        if (detail == null)
        {
            return;
        }

        var word = await context.Words
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.WordDetailId == detail.Id, cancellationToken);
        var quiz = word == null
            ? null
            : await context.Quizzes
                .AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == word.QuizId, cancellationToken);

        var changed = await enrichment.EnrichAsync(
            detail,
            word,
            quiz,
            string.IsNullOrWhiteSpace(detail.Word) ? job.Lemma : detail.Word,
            string.IsNullOrWhiteSpace(detail.TargetLanguage) ? job.Language : detail.TargetLanguage,
            cancellationToken);

        if (changed)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }
}
