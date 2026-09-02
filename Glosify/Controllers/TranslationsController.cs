using Glosify.Extensions;
using Glosify.Localization;
using Glosify.Models.ViewModels;
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
    private readonly UiTextStringLocalizer _text = new();

    public TranslationsController(ITextTranslationService translations)
    {
        _translations = translations;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var library = await _translations.GetLibraryAsync(
            User.GetUserId(),
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
        var session = await _translations.GetSessionAsync(
            id,
            User.GetUserId(),
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
        try
        {
            await _translations.DeleteSessionAsync(id, User.GetUserId(), cancellationToken);
            TempData["TranslationMessage"] = _text["Translations.SessionDeleted"].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (SavedTranslationNotFoundException)
        {
            return NotFound();
        }
    }
}
