using Glosify.Services.Translator;

namespace Glosify.Models.ViewModels;

public sealed class SavedTranslationLibraryViewModel
{
    public required SavedTranslationLibraryPage Library { get; init; }
}

public sealed class SavedTranslationDetailViewModel
{
    public required SavedTranslationDetail Translation { get; init; }
}
