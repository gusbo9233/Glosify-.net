namespace Glosify.Services;

public sealed record DictionaryEnrichmentJob(string WordDetailId, string Language, string Lemma);

public interface IDictionaryEnrichmentQueue
{
    void Enqueue(DictionaryEnrichmentJob job);
    IAsyncEnumerable<DictionaryEnrichmentJob> ReadAllAsync(CancellationToken cancellationToken);
}
