using Glosify.Extensions;
using Glosify.Models.ViewModels;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public sealed class AnkiController : Controller
{
    private readonly IAnkiCollectionService _collections;
    private readonly IAnkiStudyService _study;
    private readonly IAnkiStatisticsService _statistics;
    private readonly ILanguageContext _languages;

    public AnkiController(
        IAnkiCollectionService collections,
        IAnkiStudyService study,
        IAnkiStatisticsService statistics,
        ILanguageContext languages)
    {
        _collections = collections;
        _study = study;
        _statistics = statistics;
        _languages = languages;
    }

    [HttpGet]
    public async Task<IActionResult> Index(bool create = false, CancellationToken cancellationToken = default)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");

        return View(new AnkiIndexViewModel
        {
            Collections = await _collections.ListForLanguageAsync(
                User.GetUserId(), targetLanguage, cancellationToken),
            SourceLanguages = _languages.SupportedLanguages
                .Where(language => QuizLanguageCatalog.Find(language)?.IsLanguageLearning == true)
                .Where(language => !string.Equals(language, targetLanguage, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            TargetLanguage = targetLanguage,
            CreateDialogOpen = create,
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(CreateAnkiCollectionForm form, CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");

        if (!ModelState.IsValid)
            return RedirectToAction(nameof(Index));
        try
        {
            var collection = await _collections.CreateAsync(new(
                form.Name, form.SourceLanguage, targetLanguage, form.TimeZoneId),
                User.GetUserId(), cancellationToken);
            return RedirectToAction(nameof(Collection), new { id = collection.Id });
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
            return RedirectToAction(nameof(Index));
        }
    }

    [HttpGet]
    public async Task<IActionResult> Collection(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(id, userId, cancellationToken))
            return NotFound();
        var details = await _collections.GetDetailsAsync(id, userId, cancellationToken);
        var statistics = await _statistics.GetAsync(id, userId, cancellationToken);
        if (details is null || statistics is null)
            return NotFound();
        return View(new AnkiCollectionViewModel { Details = details, Statistics = statistics });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rename(Guid id, string name, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(id, userId, cancellationToken))
            return NotFound();
        try
        {
            if (!await _collections.RenameAsync(id, name, userId, cancellationToken))
                return NotFound();
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        return RedirectToAction(nameof(Collection), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Settings(Guid id, double desiredRetention, int newCardsPerDay,
        int maximumReviewsPerDay, string timeZoneId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(id, userId, cancellationToken))
            return NotFound();
        if (!await _collections.UpdateSettingsAsync(id, desiredRetention, newCardsPerDay,
                maximumReviewsPerDay, timeZoneId, userId, cancellationToken))
            return NotFound();
        return RedirectToAction(nameof(Collection), new { id });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(id, userId, cancellationToken))
            return NotFound();
        if (!await _collections.DeleteAsync(id, userId, cancellationToken))
            return NotFound();
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuiz(AddAnkiQuizForm form, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(form.CollectionId, userId, cancellationToken))
            return NotFound();
        try
        {
            var added = await _collections.AddQuizAsync(new(form.CollectionId, form.QuizId,
                form.WordsSourceToTarget, form.WordsTargetToSource,
                form.SentencesSourceToTarget, form.SentencesTargetToSource),
                userId, cancellationToken);
            if (!added) return NotFound();
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        return RedirectToAction(nameof(Collection), new { id = form.CollectionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> CreateFromQuiz(CreateAnkiFromQuizForm form, CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");

        var userId = User.GetUserId();
        try
        {
            var collection = await _collections.CreateFromQuizAsync(new(form.Name, form.QuizId, form.TimeZoneId, targetLanguage,
                form.WordsSourceToTarget, form.WordsTargetToSource,
                form.SentencesSourceToTarget, form.SentencesTargetToSource), userId, cancellationToken);
            if (collection is null) return NotFound();
            return RedirectToAction(nameof(Collection), new { id = collection.Id });
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
            return RedirectToAction("Settings", "Quiz", new { id = form.QuizId });
        }
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveQuiz(Guid collectionId, Guid quizId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(collectionId, userId, cancellationToken))
            return NotFound();
        if (!await _collections.RemoveQuizAsync(collectionId, quizId, userId, cancellationToken))
            return NotFound();
        return RedirectToAction(nameof(Collection), new { id = collectionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(AddAnkiItemForm form, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest();
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(form.CollectionId, userId, cancellationToken))
            return NotFound();
        try
        {
            var added = await _collections.AddItemAsync(new(form.CollectionId, form.QuizId,
                form.ItemType, form.ItemId, form.SourceToTarget, form.TargetToSource),
                userId, cancellationToken);
            if (!added) return NotFound();
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Collection), new { id = form.CollectionId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveCard(Guid id, Guid collectionId, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null || !await _collections.IsCardInOwnedLanguageAsync(
                id, collectionId, targetLanguage, userId, cancellationToken))
            return NotFound();
        if (!await _collections.RemoveCardAsync(id, userId, cancellationToken))
            return NotFound();
        return RedirectToAction(nameof(Collection), new { id = collectionId });
    }

    [HttpGet]
    public async Task<IActionResult> Study(
        Guid id,
        bool reveal = false,
        Guid? cardId = null,
        CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(id, userId, cancellationToken))
            return NotFound();
        var state = await _study.GetNextAsync(id, userId, cardId, cancellationToken);
        if (state is null) return NotFound();
        return View(new AnkiStudyViewModel
        {
            State = state,
            AnswerRevealed = reveal,
            ClientToken = Guid.NewGuid(),
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Reveal(
        Guid id,
        Guid cardId,
        CancellationToken cancellationToken)
    {
        if (!await IsCurrentLanguageCollectionAsync(id, User.GetUserId(), cancellationToken))
            return NotFound();
        return RedirectToAction(nameof(Study), new { id, cardId, reveal = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rate(RateAnkiCardForm form, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            TempData["AnkiMessage"] = "Choose Again, Hard, Good, or Easy to rate the card.";
            return RedirectToAction(nameof(Study), new { id = form.CollectionId });
        }
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(form.CollectionId, userId, cancellationToken))
            return NotFound();
        try
        {
            await _study.RateAsync(new(form.CollectionId, form.CardId, form.Rating,
                form.ClientToken, form.RowVersion, form.DurationMilliseconds),
                userId, cancellationToken);
        }
        catch (AnkiReviewConflictException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        return RedirectToAction(nameof(Study), new { id = form.CollectionId });
    }

    private Task<bool> IsCurrentLanguageCollectionAsync(
        Guid collectionId,
        string userId,
        CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        return targetLanguage is null
            ? Task.FromResult(false)
            : _collections.IsOwnedByLanguageAsync(
                collectionId, targetLanguage, userId, cancellationToken);
    }
}
