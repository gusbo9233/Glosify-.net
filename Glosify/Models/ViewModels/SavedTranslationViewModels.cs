using Glosify.Services.Translator;

namespace Glosify.Models.ViewModels;

public sealed class SavedTranslationLibraryViewModel
{
    public required SavedTranslationLibraryPage Library { get; init; }
}

public sealed class SavedTranslationSessionDetailViewModel
{
    public required SavedTranslationSessionDetailPage Session { get; init; }
}
