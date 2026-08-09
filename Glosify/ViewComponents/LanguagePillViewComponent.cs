using Glosify.Services.Language;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.ViewComponents;

/// <summary>
/// The selected-language chip in the sidebar. Reading the cookie is cheap, but keeping it
/// here is what lets the layout inject no services at all.
/// </summary>
public sealed class LanguagePillViewComponent : ViewComponent
{
    private readonly ILanguageContext _languages;

    public LanguagePillViewComponent(ILanguageContext languages)
    {
        _languages = languages;
    }

    public IViewComponentResult Invoke() => View((object?)_languages.CurrentLanguage);
}
