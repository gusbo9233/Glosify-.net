namespace Glosify.Models.Api;

public sealed class UpdateBookTranslationPreferenceRequest
{
    public string? TargetLanguage { get; set; }
}

public sealed class TranslateBookPageRequest
{
    public string? TargetLanguage { get; set; }
    public List<BookPageSourceSegmentRequest>? Segments { get; set; }
}

public sealed class BookPageSourceSegmentRequest
{
    public int Index { get; set; }
    public int ParagraphIndex { get; set; }
    public string? SourceText { get; set; }
}
