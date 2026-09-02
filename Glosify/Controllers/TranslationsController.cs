using Glosify.Extensions;
using Glosify.Localization;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Language;
using Glosify.Services.Translator;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[Route("Translations")]
public sealed class TranslationsController : Controller
{
    private const int PageSize = 24;
    private readonly ITextTranslationService _translations;
    private readonly IQuizLanguagePreferenceService _preferences;
    private readonly ILanguageContext _languageContext;
    private readonly UiTextStringLocalizer _text = new();

    public TranslationsController(
        ITextTranslationService translations,
        IQuizLanguagePreferenceService preferences,
        ILanguageContext languageContext)
    {
        _translations = translations;
        _preferences = preferences;
        _languageContext = languageContext;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (QuizLanguageCatalog.IsFreestyle(_languageContext.CurrentLanguage))
        {
            return RedirectToAction("Index", "Home");
        }
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is { IsLanguageLearning: false })
        {
            return RedirectToAction("Index", "Home");
        }
        if (language is null)
        {
            return RedirectToAction(
                "Index",
                "Languages",
                new { returnUrl = Url.Action(nameof(Index), "Translations") });
        }
        var library = await _translations.GetLibraryAsync(
            User.GetUserId(),
            language.Code,
            page,
            PageSize,
            cancellationToken);
        return View(new SavedTranslationLibraryViewModel { Library = library });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(
        Guid id,
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        if (QuizLanguageCatalog.IsFreestyle(_languageContext.CurrentLanguage))
        {
            return RedirectToAction("Index", "Home");
        }
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is { IsLanguageLearning: false })
        {
            return RedirectToAction("Index", "Home");
        }
        if (language is null)
        {
            return RedirectToAction(
                "Index",
                "Languages",
                new { returnUrl = Url.Action(nameof(Details), "Translations", new { id }) });
        }
        var session = await _translations.GetSessionAsync(
            id,
            User.GetUserId(),
            language.Code,
            page,
            PageSize,
            cancellationToken);
        return session is null
            ? NotFound()
            : View(new SavedTranslationSessionDetailViewModel { Session = session });
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        if (QuizLanguageCatalog.IsFreestyle(_languageContext.CurrentLanguage))
        {
            return RedirectToAction("Index", "Home");
        }
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is { IsLanguageLearning: false })
        {
            return RedirectToAction("Index", "Home");
        }
        if (language is null)
        {
            return RedirectToAction(
                "Index",
                "Languages",
                new { returnUrl = Url.Action(nameof(Index), "Translations") });
        }
        try
        {
            await _translations.DeleteSessionAsync(
                id,
                User.GetUserId(),
                language.Code,
                cancellationToken);
            TempData["TranslationMessage"] = _text["Translations.SessionDeleted"].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (SavedTranslationNotFoundException)
        {
            return NotFound();
        }
    }
}
