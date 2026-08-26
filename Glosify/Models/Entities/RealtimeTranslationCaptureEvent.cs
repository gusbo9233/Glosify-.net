namespace Glosify.Models.Entities;

public sealed class RealtimeTranslationCaptureEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public int Ordinal { get; set; }
    public int Sequence { get; set; }
    public string Stage { get; set; } = RealtimeTranslationCaptureStages.Scribe;
    public string Kind { get; set; } = RealtimeTranslationCaptureKinds.Partial;
    public string Text { get; set; } = string.Empty;
    public string? SourceText { get; set; }
    public string? SourceLanguage { get; set; }
    public string? TargetLanguage { get; set; }
    public bool ProviderRequest { get; set; }
    public DateTimeOffset CapturedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset StoredAt { get; set; } = DateTimeOffset.UtcNow;
    public RealtimeTranslationSession Session { get; set; } = null!;
}

public static class RealtimeTranslationCaptureStages
{
    public const string Scribe = "scribe";
    public const string Translator = "translator";
    public const string Bubble = "bubble";
}

public static class RealtimeTranslationCaptureKinds
{
    public const string Partial = "partial";
    public const string Final = "final";
}
