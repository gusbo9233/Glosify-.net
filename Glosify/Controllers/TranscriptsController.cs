using Glosify.Extensions;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Glosify.Localization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
[Route("Transcripts")]
public sealed class TranscriptsController : Controller
{
    private const int LibraryPageSize = 24;

    /// <summary>
    /// Deliberately not a local constant: the assistant reads the same pages through
    /// get_saved_transcript, so "page 3" has to mean one thing on both sides.
    /// </summary>
    private const int DetailPageSize = RealtimeTranslationTranscriptService.DetailPageSize;
    private readonly IRealtimeTranslationTranscriptService _transcripts;
    private readonly IQuizLanguagePreferenceService _preferences;
    private readonly UiTextStringLocalizer _text = new();

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
    public async Task<IActionResult> Details(Guid id, int page = 1, string? stream = null, CancellationToken cancellationToken = default)
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
            stream,
            cancellationToken);
        if (transcript is null)
        {
            return NotFound();
        }
        var spans = await _transcripts.GetPageSpansAsync(
            id,
            User.GetUserId(),
            language.Code,
            transcript.SelectedStream,
            DetailPageSize,
            cancellationToken);
        return View(new TranscriptDetailViewModel { Transcript = transcript, PageSpans = spans });
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
            TempData["TranscriptMessage"] = _text["Transcripts.Renamed"].Value;
        }
        catch (RealtimeTranslationValidationException)
        {
            TempData["TranscriptError"] = _text["Transcripts.InvalidTitle"].Value;
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
            TempData["TranscriptMessage"] = _text["Transcripts.Deleted"].Value;
            return RedirectToAction(nameof(Index));
        }
        catch (RealtimeTranslationConflictException)
        {
            TempData["TranscriptError"] = _text["Transcripts.StopBeforeDelete"].Value;
            return RedirectToAction(nameof(Details), new { id });
        }
        catch (RealtimeTranslationNotFoundException)
        {
            return NotFound();
        }
    }
}
