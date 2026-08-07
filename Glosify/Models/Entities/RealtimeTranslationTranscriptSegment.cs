namespace Glosify.Models.Entities;

public sealed class RealtimeTranslationTranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TranscriptId { get; set; }
    public Guid SessionId { get; set; }
    public int Sequence { get; set; }
    public string Stream { get; set; } = RealtimeTranslationTranscriptStreams.Source;
    public string ProviderEventKey { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public RealtimeTranslationTranscript Transcript { get; set; } = null!;
}
