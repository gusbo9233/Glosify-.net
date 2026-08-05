using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.RealtimeTranslation;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[Route("Transcripts")]
public sealed class TranscriptsController : Controller
{
    private const int LibraryPageSize = 24;
    private const int DetailPageSize = 100;
    private readonly IRealtimeTranslationTranscriptService _transcripts;
    private readonly IQuizLanguagePreferenceService _preferences;

    public TranscriptsController(
        IRealtimeTranslationTranscriptService transcripts,
        IQuizLanguagePreferenceService preferences)
    {
        _transcripts = transcripts;
        _preferences = preferences;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(int page = 1, CancellationToken cancellationToken = default)
    {
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is null)
        {
            return RedirectToAction("Index", "Languages", new { returnUrl = Url.Action(nameof(Index), "Transcripts") });
        }
        var library = await _transcripts.GetLibraryAsync(
            User.GetUserId(),
            language.Code,
            page,
            LibraryPageSize,
            cancellationToken);
        return View(new TranscriptLibraryViewModel { Library = library });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Details(Guid id, int page = 1, CancellationToken cancellationToken = default)
    {
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is null)
        {
            return RedirectToAction("Index", "Languages", new { returnUrl = Url.Action(nameof(Details), "Transcripts", new { id }) });
        }
        var transcript = await _transcripts.GetDetailAsync(
            id,
            User.GetUserId(),
            language.Code,
            page,
            DetailPageSize,
            cancellationToken);
        return transcript is null
            ? NotFound()
            : View(new TranscriptDetailViewModel { Transcript = transcript });
    }

    [HttpPost("{id:guid}/rename")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(Guid id, string title, CancellationToken cancellationToken = default)
    {
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is null)
        {
            return RedirectToAction("Index", "Languages", new { returnUrl = Url.Action(nameof(Details), "Transcripts", new { id }) });
        }
        try
        {
            await _transcripts.RenameAsync(id, User.GetUserId(), language.Code, title, cancellationToken);
            TempData["TranscriptMessage"] = "Transcript renamed.";
        }
        catch (RealtimeTranslationValidationException exception)
        {
            TempData["TranscriptError"] = exception.Message;
        }
        catch (RealtimeTranslationNotFoundException)
        {
            return NotFound();
        }
        return RedirectToAction(nameof(Details), new { id });
    }

    [HttpPost("{id:guid}/delete")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken = default)
    {
        var language = await _preferences.GetSelectedAsync(User.GetUserId(), cancellationToken);
        if (language is null)
        {
            return RedirectToAction("Index", "Languages", new { returnUrl = Url.Action(nameof(Index), "Transcripts") });
        }
        try
        {
            await _transcripts.DeleteAsync(id, User.GetUserId(), language.Code, cancellationToken);
            TempData["TranscriptMessage"] = "Transcript deleted.";
            return RedirectToAction(nameof(Index));
        }
        catch (RealtimeTranslationConflictException exception)
        {
            TempData["TranscriptError"] = exception.Message;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (RealtimeTranslationNotFoundException)
        {
            return NotFound();
        }
    }
}
