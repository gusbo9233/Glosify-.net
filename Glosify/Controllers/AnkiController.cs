using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.ViewModels;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

// One Anki collection per user and learning language. Collection-scoped actions
// resolve it from the current app language, never from a client-supplied id.
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
    [AllowAnonymous]
    public async Task<IActionResult> Index(
        bool reveal = false,
        Guid? cardId = null,
        Guid? quizId = null,
        string? search = null,
        CancellationToken cancellationToken = default)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            ViewData["GuestSample"] = "anki";
            return View(Glosify.Services.Guest.GuestSamples.For(HttpContext).Index("anki"));
        }

        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");

        var userId = User.GetUserId();
        var current = await _collections.FindForLanguageAsync(userId, targetLanguage, cancellationToken);
        var direction = current?.DefaultDirection ?? PracticeDirection.SourceToTarget;
        AnkiStudyViewModel? study = null;
        if (current is { } collection)
        {
            study = await StudyModelAsync(collection.Id, userId, targetLanguage, direction, reveal, cardId, cancellationToken);
            if (study is null)
                return NotFound();
        }
        var workspace = await _collections.GetWorkspaceAsync(userId, targetLanguage, cancellationToken, quizId, search);
        var model = new AnkiIndexViewModel
        {
            TargetLanguage = targetLanguage,
            Direction = direction,
            Workspace = workspace,
            Study = study,
        };
        return IsAjax() ? PartialView("_Workspace", model) : View(model);
    }

    // Old collection links (bookmarks, assistant messages) land on the single page.
    [HttpGet]
    public IActionResult Collection(Guid? id) => RedirectToActionPermanent(nameof(Index));

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Direction(string direction, CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");
        var collection = await CurrentCollectionAsync(cancellationToken);
        if (collection is null)
        {
            TempData["AnkiMessage"] = "Add cards before choosing a practice direction.";
            return RedirectToAction(nameof(Index));
        }
        try
        {
            await _collections.SetPracticeDirectionAsync(collection.Id, direction, User.GetUserId(), cancellationToken);
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddQuiz(AddAnkiQuizForm form, CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");
        if (!ModelState.IsValid)
            return IsAjax() ? Problem(statusCode: 400, detail: "Check the quiz settings and try again.")
                : RedirectToAction(nameof(Index), new { quizId = form.QuizId });
        var userId = User.GetUserId();
        try
        {
            var added = await _collections.AddQuizForLanguageAsync(targetLanguage, form.TimeZoneId,
                form.QuizId, form.IncludeSentences, userId, cancellationToken);
            if (!added) return NotFound();
            TempData["AnkiMessage"] = "Quiz added to Anki.";
        }
        catch (AnkiValidationException exception)
        {
            if (IsAjax()) return Problem(statusCode: 400, detail: exception.Message);
            TempData["AnkiMessage"] = exception.Message;
        }
        if (IsAjax()) return await Index(quizId: form.QuizId, cancellationToken: cancellationToken);
        return RedirectToAction(nameof(Index), new { quizId = form.QuizId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> RemoveQuiz(Guid quizId, CancellationToken cancellationToken)
    {
        var collection = await CurrentCollectionAsync(cancellationToken);
        if (collection is null || !await _collections.RemoveQuizAsync(collection.Id, quizId, User.GetUserId(), cancellationToken))
            return NotFound();
        if (IsAjax())
        {
            TempData["AnkiMessage"] = "Quiz removed from Anki.";
            return await Index(quizId: quizId, cancellationToken: cancellationToken);
        }
        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    // Changed item keys plus the antiforgery token and browser timezone.
    [RequestFormLimits(ValueCountLimit = AnkiCollectionService.MaximumMembershipChanges + 2)]
    public async Task<IActionResult> Items(SaveAnkiItemsForm form, CancellationToken cancellationToken,
        [FromQuery] Guid? quizId = null, [FromQuery] string? search = null)
    {
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");
        if (!ModelState.IsValid)
            return IsAjax() ? Problem(statusCode: 400, detail: "Check the selected items and try again.")
                : RedirectToAction(nameof(Index), new { quizId, search });
        var add = ParseKeys(form.Add);
        var remove = ParseKeys(form.Remove);
        if (add is null || remove is null)
            return BadRequest();
        if (add.Count + remove.Count == 0)
            return IsAjax() ? await Index(quizId: quizId, search: search, cancellationToken: cancellationToken)
                : RedirectToAction(nameof(Index), new { quizId, search });

        var userId = User.GetUserId();
        try
        {
            if (!await _collections.SetMembershipForLanguageAsync(targetLanguage, form.TimeZoneId,
                add, remove, userId, cancellationToken))
                return NotFound();
            TempData["AnkiMessage"] = (add.Count, remove.Count) switch
            {
                (> 0, 0) => $"Added {add.Count} {(add.Count == 1 ? "item" : "items")} to Anki.",
                (0, > 0) => $"Removed {remove.Count} {(remove.Count == 1 ? "item" : "items")} from Anki.",
                _ => $"Added {add.Count} and removed {remove.Count}.",
            };
        }
        catch (AnkiValidationException exception)
        {
            if (IsAjax()) return Problem(statusCode: 400, detail: exception.Message);
            TempData["AnkiMessage"] = exception.Message;
        }
        if (IsAjax()) return await Index(quizId: quizId, search: search, cancellationToken: cancellationToken);
        return RedirectToAction(nameof(Index), new { quizId, search });
    }

    // Used by the quiz page's "Add to Anki" dialog.
    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddItem(AddAnkiItemForm form, string? returnUrl, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid) return BadRequest();
        var targetLanguage = _languages.CurrentLanguage;
        if (targetLanguage is null)
            return RedirectToAction("Index", "Languages");
        var userId = User.GetUserId();
        try
        {
            var added = await _collections.AddItemForLanguageAsync(targetLanguage, form.TimeZoneId,
                new(form.QuizId, form.ItemType, form.ItemId), userId, cancellationToken);
            if (!added) return NotFound();
        }
        catch (AnkiValidationException exception)
        {
            TempData["AnkiMessage"] = exception.Message;
        }
        if (!string.IsNullOrWhiteSpace(returnUrl) && Url.IsLocalUrl(returnUrl)) return LocalRedirect(returnUrl);
        return RedirectToAction(nameof(Index));
    }

    // The session partial for in-place updates. Full-page requests (including
    // old /Anki/Study/{id} links) are sent to the single Anki page.
    [HttpGet]
    public async Task<IActionResult> Study(
        Guid? id = null,
        bool reveal = false,
        Guid? cardId = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAjax())
            return RedirectToAction(nameof(Index), new { reveal = reveal ? true : (bool?)null, cardId });
        var targetLanguage = _languages.CurrentLanguage;
        var collection = await CurrentCollectionAsync(cancellationToken);
        if (targetLanguage is null || collection is null)
            return NotFound();
        var model = await StudyModelAsync(collection.Id, User.GetUserId(), targetLanguage,
            collection.DefaultDirection, reveal, cardId, cancellationToken);
        if (model is null) return NotFound();
        if (reveal && cardId.HasValue && model.State.Card?.CardId != cardId.Value)
            return Problem(statusCode: 409, detail: "This card changed. Reload Anki to continue.");
        return PartialView("_StudySession", model);
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
        return RedirectToAction(nameof(Study), new { cardId, reveal = true });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Rate(RateAnkiCardForm form, CancellationToken cancellationToken)
    {
        var clientToken = form.ClientToken.GetValueOrDefault();
        if (clientToken == Guid.Empty)
            ModelState.AddModelError(nameof(form.ClientToken), "A review token is required.");

        if (!ModelState.IsValid)
        {
            TempData["AnkiMessage"] = ModelState[nameof(form.ClientToken)]?.Errors.Count > 0
                ? "Reload the card before rating it."
                : "Choose Again, Hard, Good, or Easy to rate the card.";
            return RedirectToAction(nameof(Study));
        }
        var userId = User.GetUserId();
        if (!await IsCurrentLanguageCollectionAsync(form.CollectionId, userId, cancellationToken))
            return NotFound();
        try
        {
            var rated = await _study.RateAsync(new(form.CollectionId, form.CardId, form.Rating,
                clientToken, form.RowVersion, form.DurationMilliseconds),
                userId, cancellationToken);
            if (!rated) return Problem(statusCode: 409, detail: "This card is no longer available. Reload Anki to continue.");
        }
        catch (AnkiReviewConflictException exception)
        {
            if (IsAjax()) return Problem(statusCode: 409, detail: exception.Message);
            TempData["AnkiMessage"] = exception.Message;
        }
        return RedirectToAction(nameof(Study));
    }

    private async Task<AnkiStudyViewModel?> StudyModelAsync(Guid collectionId, string userId, string targetLanguage,
        string direction, bool reveal, Guid? cardId, CancellationToken cancellationToken)
    {
        var state = await _study.GetNextAsync(collectionId, userId, direction, cardId, cancellationToken);
        if (state is null) return null;
        return new AnkiStudyViewModel
        {
            State = state,
            TargetLanguage = targetLanguage,
            Statistics = await _statistics.ReadSnapshotAsync(collectionId, userId, state.Direction, cancellationToken),
            AnswerRevealed = reveal && state.Card is not null && state.Card.CardId == cardId,
            ClientToken = Guid.NewGuid(),
        };
    }

    private async Task<Models.Entities.AnkiCollection?> CurrentCollectionAsync(CancellationToken cancellationToken)
    {
        var targetLanguage = _languages.CurrentLanguage;
        return targetLanguage is null
            ? null
            : await _collections.FindForLanguageAsync(User.GetUserId(), targetLanguage, cancellationToken);
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

    private bool IsAjax() => Request.Headers.XRequestedWith == "XMLHttpRequest";

    private static List<AnkiItemKey>? ParseKeys(IEnumerable<string> keys)
    {
        var parsed = new List<AnkiItemKey>();
        foreach (var key in keys.Distinct(StringComparer.Ordinal))
        {
            // Word ids are opaque strings, so only the first two separators count.
            var parts = key.Split(':', 3);
            if (parts.Length != 3 || parts[0] is not (PracticeItemType.Words or PracticeItemType.Sentences)
                || !Guid.TryParse(parts[1], out var quizId) || string.IsNullOrWhiteSpace(parts[2]))
                return null;
            parsed.Add(new AnkiItemKey(quizId, parts[0], parts[2]));
        }
        return parsed;
    }
}
