using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Tests;

/// <summary>
/// Test double that runs enrichment inline on Enqueue, so tests asserting on populated
/// WordDetail rows after AddWord don't have to wait for the background service.
/// </summary>
internal sealed class SynchronousEnrichmentQueue : IDictionaryEnrichmentQueue
{
    private readonly GlosifyContext _context;
    private readonly IDictionaryService _dictionary;

    public SynchronousEnrichmentQueue(GlosifyContext context, IDictionaryService dictionary)
    {
        _context = context;
        _dictionary = dictionary;
    }

    public void Enqueue(DictionaryEnrichmentJob job)
    {
        EnrichAsync(job).GetAwaiter().GetResult();
    }

    public async IAsyncEnumerable<DictionaryEnrichmentJob> ReadAllAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Task.CompletedTask;
        yield break;
    }

    private async Task EnrichAsync(DictionaryEnrichmentJob job)
    {
        var match = await _dictionary.FindManualDictionaryMatchAsync(job.Language, job.Lemma);
        if (match == null) return;

        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == job.WordDetailId);
        if (detail == null) return;

        if (detail.Properties == "{}") detail.Properties = match.Properties;
        if (detail.Variants == "[]") detail.Variants = match.Variants;
        if (string.IsNullOrWhiteSpace(detail.Explanation) && !string.IsNullOrWhiteSpace(match.Description))
            detail.Explanation = match.Description;
        if (string.IsNullOrWhiteSpace(detail.ExampleSentence) && !string.IsNullOrWhiteSpace(match.ExampleSentence))
            detail.ExampleSentence = match.ExampleSentence;

        await _context.SaveChangesAsync();
    }
}
