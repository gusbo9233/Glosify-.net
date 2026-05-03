using System.Threading.Channels;

namespace Glosify.Services;

public sealed class DictionaryEnrichmentQueue : IDictionaryEnrichmentQueue
{
    // Bounded so a runaway producer can't OOM the host. DropOldest is fine for an
    // enrichment queue: if we drop a job, WordDetailsController.Details still has
    // the on-demand fallback that asks Gemini on first view.
    private readonly Channel<DictionaryEnrichmentJob> _channel = Channel.CreateBounded<DictionaryEnrichmentJob>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(DictionaryEnrichmentJob job)
    {
        _channel.Writer.TryWrite(job);
    }

    public IAsyncEnumerable<DictionaryEnrichmentJob> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
