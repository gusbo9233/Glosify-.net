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
        Guid requestId,
        string? sourceText,
        string? translatedText,
        string? sourceLanguage,
        string? detectedSourceLanguage,
        string? targetLanguage,
        string? preferences,
        CancellationToken cancellationToken = default);

    Task<SavedTranslationLibraryPage> GetLibraryAsync(
        string userId,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);

    Task<SavedTranslationDetail?> GetDetailAsync(
        Guid id,
        string userId,
        CancellationToken cancellationToken = default);

    Task DeleteAsync(Guid id, string userId, CancellationToken cancellationToken = default);
}
