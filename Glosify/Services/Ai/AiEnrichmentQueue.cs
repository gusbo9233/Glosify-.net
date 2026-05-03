using System.Threading.Channels;

namespace Glosify.Services;

public sealed class AiEnrichmentQueue : IAiEnrichmentQueue
{
    // Bounded so a runaway producer can't OOM the host. DropOldest is fine for an
    // enrichment queue: if we drop a job, WordDetailsController.Details still has
    // the on-demand fallback that asks Gemini on first view.
    private readonly Channel<AiEnrichmentJob> _channel = Channel.CreateBounded<AiEnrichmentJob>(
        new BoundedChannelOptions(1024)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });

    public void Enqueue(AiEnrichmentJob job)
    {
        _channel.Writer.TryWrite(job);
    }

    public IAsyncEnumerable<AiEnrichmentJob> ReadAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
