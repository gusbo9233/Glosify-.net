using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed record TranslatorLanguageDto(string Code, string Name, string NativeName);

public sealed record TranslatorCatalogDto(
    IReadOnlyList<TranslatorLanguageDto> Languages,
    IReadOnlyList<TranslatorLanguageDto> SourceLanguages,
    int MaxSourceCharacters,
    int MaxPreferenceCharacters,
    int MaxTranslatedCharacters);

public sealed class TranslateTextRequest
{
    [Required, StringLength(8_000, MinimumLength = 1)]
    public string? SourceText { get; set; }

    [Required, StringLength(8)]
    public string? SourceLanguage { get; set; }

    [Required, StringLength(8)]
    public string? TargetLanguage { get; set; }

    [StringLength(500)]
    public string? Preferences { get; set; }
}

public sealed record TranslateTextResponse(
    string SourceText,
    string SourceLanguage,
    string? DetectedSourceLanguage,
    string TargetLanguage,
    string TranslatedText,
    int RemainingCredits);

public sealed class SaveTranslationRequest
{
    public Guid RequestId { get; set; }

    [Required, StringLength(8_000, MinimumLength = 1)]
    public string? SourceText { get; set; }

    [Required, StringLength(16_000, MinimumLength = 1)]
    public string? TranslatedText { get; set; }

    [Required, StringLength(8)]
    public string? SourceLanguage { get; set; }

    [StringLength(8)]
    public string? DetectedSourceLanguage { get; set; }

    [Required, StringLength(8)]
    public string? TargetLanguage { get; set; }

    [StringLength(500)]
    public string? Preferences { get; set; }
}

public sealed record SavedTranslationCreatedDto(Guid Id, DateTimeOffset CreatedAt, string HistoryUrl);
