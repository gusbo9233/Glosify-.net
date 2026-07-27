namespace Glosify.Models.Library;

public sealed class BookPageTranslation
{
    public Guid Id { get; set; }
    public Guid BookPageId { get; set; }
    public string TargetLanguage { get; set; } = string.Empty;
    public string SourceTextHash { get; set; } = string.Empty;
    public string? DetectedSourceLanguage { get; set; }
    public int SchemaVersion { get; set; }
    public string Model { get; set; } = string.Empty;
    public string SegmentsJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }

    public BookPage BookPage { get; set; } = null!;
}
