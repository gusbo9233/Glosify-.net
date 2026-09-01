namespace Glosify.Services.Translator;

public sealed class TextTranslationAiResponse
{
    public required string? DetectedSourceLanguage { get; set; }
    public required string Translation { get; set; }
}

public sealed record TextTranslationResult(
    string SourceText,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string TranslatedText,
    int AvailableCredits);

public sealed record SavedTranslationResult(Guid Id, DateTimeOffset CreatedAt);

public sealed record SavedTranslationListItem(
    Guid Id,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string SourcePreview,
    string TranslationPreview,
    DateTimeOffset CreatedAt);

public sealed record SavedTranslationLibraryPage(
    IReadOnlyList<SavedTranslationListItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record SavedTranslationDetail(
    Guid Id,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string? Preferences,
    string SourceText,
    string TranslatedText,
    DateTimeOffset CreatedAt);

public sealed class TextTranslationValidationException(string message) : ArgumentException(message);

public sealed class SavedTranslationNotFoundException()
    : InvalidOperationException("Saved translation not found.");
