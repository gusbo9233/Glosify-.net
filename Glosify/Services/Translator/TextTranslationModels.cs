namespace Glosify.Services.Translator;

public sealed class TextTranslationAiResponse
{
    public required string? DetectedSourceLanguage { get; set; }
    public required string Translation { get; set; }
}

public sealed record TextTranslationResult(
    Guid TranslationOperationId,
    string SourceText,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string TranslatedText,
    int AvailableCredits);

public sealed record SavedTranslationResult(
    Guid Id,
    Guid SessionId,
    DateTimeOffset CreatedAt);

public sealed record SavedTranslationSessionListItem(
    Guid Id,
    string Title,
    string SourcePreview,
    string TranslationPreview,
    int TranslationCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SavedTranslationLibraryPage(
    IReadOnlyList<SavedTranslationSessionListItem> Items,
    int Page,
    int PageSize,
    int TotalCount,
    int TotalTranslations);

public sealed record SavedTranslationDetail(
    Guid Id,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string? Preferences,
    string SourceText,
    string TranslatedText,
    DateTimeOffset CreatedAt);

public sealed record SavedTranslationSessionDetailPage(
    Guid Id,
    string Title,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<SavedTranslationDetail> Translations,
    int Page,
    int PageSize,
    int TotalCount);

public sealed class TextTranslationValidationException(string message) : ArgumentException(message);

public sealed class SavedTranslationNotFoundException()
    : InvalidOperationException("Saved translation not found.");
