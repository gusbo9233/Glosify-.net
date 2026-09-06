using Glosify.Models.Api;

namespace Glosify.Services.Translator;

public interface ITextTranslationService
{
    TranslatorCatalogDto GetCatalog();

    Task<TextTranslationResult> TranslateAsync(
        string userId,
        string? sourceText,
        string? sourceLanguage,
        string? targetLanguage,
        string? preferences,
        CancellationToken cancellationToken = default);

    Task<SavedTranslationResult> SaveAsync(
        string userId,
        Guid clientSessionId,
        Guid requestId,
        Guid translationOperationId,
        string? languageCode,
        string? sourceText,
        string? translatedText,
        string? sourceLanguage,
        string? detectedSourceLanguage,
        string? targetLanguage,
        string? preferences,
        CancellationToken cancellationToken = default);

    Task<SavedTranslationLibraryPage> GetLibraryAsync(
        string userId,
        string languageCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<SavedTranslationSessionDetailPage?> GetSessionAsync(
        Guid id,
        string userId,
        string languageCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task DeleteSessionAsync(
        Guid id,
        string userId,
        string languageCode,
        CancellationToken cancellationToken = default);
}
