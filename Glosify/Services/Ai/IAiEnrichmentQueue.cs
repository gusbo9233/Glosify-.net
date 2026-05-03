namespace Glosify.Services;

public sealed record AiEnrichmentJob(string WordDetailId, string Language, string Lemma);

public interface IAiEnrichmentQueue
{
    void Enqueue(AiEnrichmentJob job);
    IAsyncEnumerable<AiEnrichmentJob> ReadAllAsync(CancellationToken cancellationToken);
}
