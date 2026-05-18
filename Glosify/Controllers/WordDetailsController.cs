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

    public WordDetailsController(
        IWordDetailService wordDetailService,
        IWordDetailViewModelService viewModelService,
        IWordDetailEnrichmentService enrichmentService)
    {
        _wordDetailService = wordDetailService;
        _viewModelService = viewModelService;
        _enrichmentService = enrichmentService;
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

        return Json(new { ok = true, lemma = owned.Word.Lemma, isEnriched });
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
