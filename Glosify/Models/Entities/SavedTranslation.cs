namespace Glosify.Models.Entities;

public sealed class SavedTranslation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string UserId { get; set; } = string.Empty;
    public Guid RequestId { get; set; }
    public string SourceLanguage { get; set; } = string.Empty;
    public string? DetectedSourceLanguage { get; set; }
    public string TargetLanguage { get; set; } = string.Empty;
    public string? Preferences { get; set; }
    public string SourceText { get; set; } = string.Empty;
    public string TranslatedText { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
