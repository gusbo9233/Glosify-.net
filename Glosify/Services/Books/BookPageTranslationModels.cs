using Glosify.Services.Language;

namespace Glosify.Services.Books;

public sealed record BookPageSourceSegment(int Index, int ParagraphIndex, string SourceText);

public sealed record BookPageTranslationSegment(
    int Index,
    int ParagraphIndex,
    string SourceText,
    string Translation);

public sealed record BookPageTranslationResult(
    Guid DocumentId,
    int PageNumber,
    string TargetLanguage,
    string? DetectedSourceLanguage,
    bool Cached,
    DateTimeOffset GeneratedAt,
    IReadOnlyList<BookPageTranslationSegment> Segments);

public sealed class BookPageTranslationAiResponse
{
    public required string? DetectedSourceLanguage { get; set; }
    public required List<BookPageTranslationAiSegment> Segments { get; set; }
}

public sealed class BookPageTranslationAiSegment
{
    public required int Index { get; set; }
    public required string Translation { get; set; }
}

public sealed class BookPageTranslationNotFoundException : InvalidOperationException
{
    public BookPageTranslationNotFoundException() : base("Book page not found.")
    {
    }
}

public sealed class BookPageTranslationValidationException(string message) : ArgumentException(message);

public sealed class BookPageTranslationUnavailableException(string message) : InvalidOperationException(message);

public static class BookTranslationLanguages
{
    public static IReadOnlyList<string> All { get; } =
        QuizLanguageCatalog.All.Select(language => language.Name).ToArray();

    public static bool TryCanonicalize(string? language, out string canonical)
    {
        canonical = QuizLanguageCatalog.Find(language)?.Name ?? string.Empty;
        return canonical.Length > 0;
    }
}
