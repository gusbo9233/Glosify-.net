namespace Glosify.Models.Entities;

public sealed class RealtimeTranslationTranscript
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string SourceKind { get; set; } = RealtimeTranslationTranscriptSourceKinds.Subtitles;
    public Guid? AvatarConversationId { get; set; }
    public string TargetLanguage { get; set; } = string.Empty;
    public string Stream { get; set; } = RealtimeTranslationTranscriptStreams.Translation;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    public ICollection<RealtimeTranslationSession> Sessions { get; set; } = [];
    public ICollection<RealtimeTranslationTranscriptSegment> Segments { get; set; } = [];
}

public static class RealtimeTranslationTranscriptStreams
{
    public const string Translation = "translation";
    public const string Source = "source";
}

public static class RealtimeTranslationTranscriptSourceKinds
{
    public const string Subtitles = "subtitles";
    public const string Avatar = "avatar";
}
