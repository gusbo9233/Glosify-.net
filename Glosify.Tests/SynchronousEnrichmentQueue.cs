using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Tests;

/// <summary>
/// Test double that runs Gemini-backed enrichment inline on Enqueue, so tests asserting on
/// populated WordDetail rows after AddWord don't have to wait for the background service.
/// </summary>
internal sealed class SynchronousEnrichmentQueue : IAiEnrichmentQueue
{
    private readonly GlosifyContext _context;
    private readonly IWordDetailEnrichmentService _enrichment;

    public SynchronousEnrichmentQueue(GlosifyContext context, IWordDetailEnrichmentService enrichment)
    {
        _context = context;
        _enrichment = enrichment;
    }

    public void Enqueue(AiEnrichmentJob job)
    {
        EnrichAsync(job).GetAwaiter().GetResult();
    }

    public async IAsyncEnumerable<AiEnrichmentJob> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task EnrichAsync(AiEnrichmentJob job)
    {
        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == job.WordDetailId);
        if (detail == null) return;

        var word = await _context.Words.AsNoTracking().FirstOrDefaultAsync(w => w.WordDetailId == detail.Id);
        var quiz = word == null
            ? null
            : await _context.Quizzes.AsNoTracking().FirstOrDefaultAsync(q => q.Id == word.QuizId);

        var changed = await _enrichment.EnrichAsync(
            detail,
            word,
            quiz,
            string.IsNullOrWhiteSpace(detail.Word) ? job.Lemma : detail.Word,
            string.IsNullOrWhiteSpace(detail.TargetLanguage) ? job.Language : detail.TargetLanguage);
        if (changed)
        {
            await _context.SaveChangesAsync();
        }
    }
}
