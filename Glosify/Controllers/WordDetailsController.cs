using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class WordDetailsController : Controller
{
    private readonly IWordDetailService _wordDetailService;
    private readonly IWordDetailViewModelService _viewModelService;
    private readonly IWordDetailEnrichmentService _enrichmentService;
    private readonly IVocabularyGenerationService _vocabularyGenerator;

    public WordDetailsController(
        IWordDetailService wordDetailService,
        IWordDetailViewModelService viewModelService,
        IWordDetailEnrichmentService enrichmentService,
        IVocabularyGenerationService vocabularyGenerator)
    {
        _wordDetailService = wordDetailService;
        _viewModelService = viewModelService;
        _enrichmentService = enrichmentService;
        _vocabularyGenerator = vocabularyGenerator;
    }

    public async Task<IActionResult> Index()
    {
        var userId = User.GetUserId();
        var wordDetails = await _wordDetailService.ListForUserAsync(userId);
        return View(wordDetails);
    }

    public async Task<IActionResult> Details(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var model = await _viewModelService.BuildAsync(id, userId);
        if (model == null)
        {
            return NotFound();
        }

        return View(model);
    }

    public IActionResult Create() => View(new CreateWordDetailInput());

    [HttpPost]
    public async Task<IActionResult> Generate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedWithWordAsync(id, userId);
        if (owned == null)
        {
            return NotFound();
        }

        var changed = await _enrichmentService.EnrichAsync(
            owned.Detail,
            owned.Word,
            owned.Quiz,
            string.IsNullOrWhiteSpace(owned.Detail.Word) ? owned.Word.Lemma : owned.Detail.Word,
            string.IsNullOrWhiteSpace(owned.Detail.TargetLanguage) ? owned.Quiz.TargetLanguage : owned.Detail.TargetLanguage);

        if (changed)
        {
            await _wordDetailService.SaveChangesAsync();
        }

        var detail = owned.Detail;
        var isEnriched = !string.IsNullOrWhiteSpace(detail.Explanation)
            && !string.IsNullOrWhiteSpace(detail.ExampleSentence)
            && detail.Properties != "{}"
            && detail.Variants != "[]";

        return Json(new { ok = true, word = owned.Word.Lemma, isEnriched });
    }

    [HttpPost]
    public async Task<IActionResult> GenerateForWord(string id, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedWordAsync(id, userId, cancellationToken);
        if (owned == null)
        {
            return NotFound();
        }

        var cached = await _wordDetailService.FindCachedAsync(
            owned.Quiz.SourceLanguage,
            owned.Quiz.TargetLanguage,
            owned.Word.Lemma,
            owned.Word.Translation,
            cancellationToken);
        if (cached != null)
        {
            owned.Word.WordDetailId = cached.Id;
            await _wordDetailService.SaveChangesAsync(cancellationToken);
            return Json(new
            {
                ok = true,
                word = owned.Word.Lemma,
                wordDetailId = cached.Id,
                detailsUrl = Url.Action(nameof(Details), new { id = cached.Id }),
                generateUrl = Url.Action(nameof(Generate), new { id = cached.Id }),
                isEnriched = IsEnriched(cached)
            });
        }

        var generated = await _vocabularyGenerator.GenerateWordDetailAsync(
            owned.Word.Lemma,
            owned.Word.Translation,
            owned.Quiz.SourceLanguage,
            owned.Quiz.TargetLanguage,
            cancellationToken);
        if (generated == null)
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant });
        }

        var detailWord = string.IsNullOrWhiteSpace(generated.Word)
            ? owned.Word.Lemma
            : generated.Word.Trim();
        var previousWordDetailId = owned.Word.WordDetailId;
        var detail = await _wordDetailService.GetOrCreateAndLinkAsync(
            owned.Word,
            owned.Quiz,
            detailWord,
            cancellationToken);
        var changed = _enrichmentService.ApplyGenerated(detail, generated);
        if (changed || !string.Equals(previousWordDetailId, detail.Id, StringComparison.Ordinal))
        {
            await _wordDetailService.SaveChangesAsync(cancellationToken);
        }

        return Json(new
        {
            ok = true,
            word = owned.Word.Lemma,
            wordDetailId = detail.Id,
            detailsUrl = Url.Action(nameof(Details), new { id = detail.Id }),
            generateUrl = Url.Action(nameof(Generate), new { id = detail.Id }),
            isEnriched = IsEnriched(detail)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Regenerate(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedWithWordAsync(id, userId);
        if (owned == null)
        {
            return NotFound();
        }

        var changed = await _enrichmentService.EnrichAsync(
            owned.Detail,
            owned.Word,
            owned.Quiz,
            string.IsNullOrWhiteSpace(owned.Detail.Word) ? owned.Word.Lemma : owned.Detail.Word,
            string.IsNullOrWhiteSpace(owned.Detail.TargetLanguage) ? owned.Quiz.TargetLanguage : owned.Detail.TargetLanguage,
            force: true);

        if (changed)
        {
            await _wordDetailService.SaveChangesAsync();
            TempData["WordDetailMessage"] = "Word details regenerated.";
        }
        else
        {
            TempData["WordDetailMessage"] = "No regenerated details were returned.";
        }

        return RedirectToAction(nameof(Details), new { id });
    }

    private static bool IsEnriched(WordDetail detail)
    {
        return !string.IsNullOrWhiteSpace(detail.Explanation)
            && !string.IsNullOrWhiteSpace(detail.ExampleSentence)
            && detail.Properties != "{}"
            && detail.Variants != "[]";
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateWordDetailInput input)
    {
        if (!ModelState.IsValid)
        {
            return View(input);
        }

        await _wordDetailService.CreateAsync(input);
        return RedirectToAction(nameof(Index));
    }

    public async Task<IActionResult> Edit(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedAsync(id, userId);
        if (owned == null)
        {
            return NotFound();
        }
        return View(owned.Detail);
    }

    [HttpPost]
    public async Task<IActionResult> Edit(string id, EditWordDetailInput input)
    {
        if (id != input.Id)
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var updated = await _wordDetailService.UpdateAsync(input, userId);
        return updated ? RedirectToAction(nameof(Index)) : NotFound();
    }

    public async Task<IActionResult> Delete(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return NotFound();
        }

        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedAsync(id, userId);
        if (owned == null)
        {
            return NotFound();
        }

        return View(owned.Detail);
    }

    [HttpPost, ActionName("Delete")]
    public async Task<IActionResult> DeleteConfirmed(string id)
    {
        var userId = User.GetUserId();
        var owned = await _wordDetailService.LoadOwnedAsync(id, userId);
        if (owned == null)
        {
            return NotFound();
        }

        if (await _wordDetailService.HasReferencesAsync(id))
        {
            return Conflict("Shared word details cannot be deleted while words reference them.");
        }

        await _wordDetailService.DeleteAsync(id, userId);
        return RedirectToAction(nameof(Index));
    }
}
