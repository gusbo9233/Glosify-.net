using Glosify.Extensions;
using Glosify.Filters;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.Requests;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Ai;
using Glosify.Services.CustomQuizzes;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Glosify.Localization;
using Glosify.Services.Anki;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class QuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly ICollectionService _collectionService;
    private readonly IWordService _wordService;
    private readonly IImageTextExtractionService _imageTextExtractionService;
    private readonly ILanguageContext _languageContext;
    private readonly ICustomQuizService _customQuizService;
    private readonly UiTextStringLocalizer _text = new();
    private readonly IAnkiCollectionService _ankiCollections;

    public QuizController(
        IQuizService quizService,
        ICollectionService collectionService,
        IWordService wordService,
        IImageTextExtractionService imageTextExtractionService,
        ILanguageContext languageContext,
        ICustomQuizService customQuizService,
        IAnkiCollectionService ankiCollections)
    {
        _quizService = quizService;
        _collectionService = collectionService;
        _wordService = wordService;
        _imageTextExtractionService = imageTextExtractionService;
        _languageContext = languageContext;
        _customQuizService = customQuizService;
        _ankiCollections = ankiCollections;
    }

    public async Task<IActionResult> Index(CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        if (_languageContext.CurrentLanguage == null)
            return RedirectToAction("Index", "Languages");

        var language = _languageContext.CurrentLanguage;
        return View(await BuildQuizIndexViewModelAsync(userId, language, null, cancellationToken: cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Collection(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        var collection = await _collectionService.GetCollectionAsync(id, userId, cancellationToken: cancellationToken);
        if (collection == null || !string.Equals(collection.Language, language, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index));

        return View(nameof(Index), await BuildQuizIndexViewModelAsync(userId, language, collection, cancellationToken: cancellationToken));
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var selectedQuiz = await _quizService.GetQuizByIdAsync(id, userId, cancellationToken: cancellationToken);
        if (selectedQuiz == null)
            return RedirectToAction(nameof(Index));

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");
        if (!string.Equals(selectedQuiz.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index));

        var words = await _wordService.GetWordsAsync(selectedQuiz.Id, cancellationToken: cancellationToken);
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id, cancellationToken: cancellationToken);

        var ankiCollections = (await _ankiCollections.ListAsync(userId, cancellationToken))
            .Where(collection => string.Equals(collection.SourceLanguage, selectedQuiz.SourceLanguage, StringComparison.OrdinalIgnoreCase)
                && string.Equals(collection.TargetLanguage, selectedQuiz.TargetLanguage, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return View(new QuizWorkspaceViewModel
        {
            SelectedQuiz = QuizCard.From(selectedQuiz),
            Words = words.Select(WordRow.From).ToList(),
            CustomQuizzes = await _customQuizService.ListForQuizAsync(selectedQuiz.Id, cancellationToken: cancellationToken),
            Sentences = sentences.Select(s => new QuizSentenceViewModel
            {
                Id = s.Id,
                Text = s.Text,
                Translation = s.Translation,
                WordCount = s.WordCount
            }).ToList(),
            AnkiCollections = ankiCollections,
        });
    }

    [HttpPost]
    public async Task<IActionResult> AddWord(AddWordInput input, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        if (!ModelState.IsValid)
        {
            TempData[NotificationKeys.Quiz] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Details), new { id = input.QuizId });
        }

        var quiz = await _quizService.GetQuizByIdAsync(input.QuizId, userId, cancellationToken: cancellationToken);
        if (quiz == null)
            return RedirectToAction(nameof(Index));

        await _wordService.AddWordAsync(
            input.QuizId,
            input.Word,
            input.Translation,
            cancellationToken);

        return RedirectToAction(nameof(Details), new { id = input.QuizId });
    }

    [HttpPost]
    [RequestSizeLimit(8 * 1024 * 1024)]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> ExtractTextFromImage(Guid quizId, IFormFile? image, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId, cancellationToken: cancellationToken);
        if (quiz == null)
            return NotFound(new { error = "Quiz not found." });

        if (image == null || image.Length == 0)
            return BadRequest(new { error = "Take or choose a photo first." });

        if (image.Length > 8 * 1024 * 1024)
            return BadRequest(new { error = "Choose an image under 8 MB." });

        if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Choose an image file." });

        await using var stream = image.OpenReadStream();
        var text = await _imageTextExtractionService.ExtractTextAsync(
            userId,
            stream,
            image.ContentType,
            quiz.SourceLanguage,
            quiz.TargetLanguage,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(text))
            return UnprocessableEntity(new { error = "No readable text was found in that image." });

        return Json(new { text });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteWord(string id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var deleted = await _wordService.DeleteWordAsync(id, userId, cancellationToken: cancellationToken);
        if (deleted == null)
            return RedirectToAction(nameof(Index));

        TempData[NotificationKeys.Quiz] = _text["Quiz.DeletedWord", deleted.Lemma].Value;
        return RedirectToAction(nameof(Details), new { id = deleted.QuizId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> DeleteSentence(Guid id, CancellationToken cancellationToken = default)
    {
        var deleted = await _wordService.DeleteSentenceAsync(id, User.GetUserId(), cancellationToken);
        if (deleted is null) return NotFound();
        TempData[NotificationKeys.Quiz] = _text["Quiz.DeletedSentence"].Value;
        return RedirectToAction(nameof(Details), new { id = deleted.QuizId });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteQuiz(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var deleted = await _quizService.DeleteQuizAsync(id, userId, cancellationToken: cancellationToken);
        if (deleted != null)
        {
            TempData[NotificationKeys.Quiz] = _text["Quiz.DeletedNamed", deleted.Name].Value;
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetQuizVisibility(Guid id, bool isPublic, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var updated = await _quizService.SetQuizPublicAsync(id, userId, isPublic, cancellationToken: cancellationToken);
        TempData[NotificationKeys.Quiz] = updated
            ? isPublic ? _text["Quiz.NowPublic"].Value : _text["Quiz.NowPrivate"].Value
            : _text["Quiz.VisibilityFailed"].Value;

        return updated
            ? RedirectToAction(nameof(Details), new { id })
            : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetCollectionVisibility(Guid id, bool isPublic, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var updated = await _collectionService.SetCollectionPublicAsync(id, userId, isPublic, cancellationToken: cancellationToken);
        TempData[NotificationKeys.Quiz] = updated
            ? isPublic ? _text["Quiz.CollectionNowPublic"].Value : _text["Quiz.CollectionNowPrivate"].Value
            : _text["Quiz.CollectionVisibilityFailed"].Value;

        return updated
            ? RedirectToAction(nameof(Collection), new { id })
            : RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateQuizInput input, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        if (QuizLanguageCatalog.IsFreestyle(language))
        {
            input.SourceLanguage = QuizLanguageCatalog.FreestyleName;
            input.TargetLanguage = QuizLanguageCatalog.FreestyleName;
            ModelState.Remove(nameof(input.SourceLanguage));
            ModelState.Remove(nameof(input.TargetLanguage));
        }

        if (!ModelState.IsValid)
        {
            TempData[NotificationKeys.Quiz] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToLibrary(input.CollectionId);
        }

        var quiz = await _quizService.CreateQuizAsync(input.Name, input.SourceLanguage, language, userId, input.CollectionId, cancellationToken: cancellationToken);
        return RedirectToAction(nameof(Details), new { id = quiz.Id });
    }

    [HttpPost]
    public async Task<IActionResult> CreateCollection(CreateCollectionInput input, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        if (!ModelState.IsValid)
        {
            TempData[NotificationKeys.Quiz] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToLibrary(input.ParentCollectionId);
        }

        try
        {
            var collection = await _collectionService.CreateCollectionAsync(input.Name, language, userId, input.ParentCollectionId, cancellationToken: cancellationToken);
            TempData[NotificationKeys.Quiz] = _text["Quiz.CollectionCreated", collection.Name].Value;
        }
        catch (InvalidOperationException)
        {
            TempData[NotificationKeys.Quiz] = _text["Quiz.CollectionCreateFailed"].Value;
        }

        return RedirectToLibrary(input.ParentCollectionId);
    }

    [HttpPost]
    public async Task<IActionResult> MoveQuizToCollection(Guid quizId, Guid? collectionId, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var moved = await _collectionService.MoveQuizToCollectionAsync(quizId, collectionId, userId, cancellationToken: cancellationToken);
        if (!moved)
        {
            return BadRequest(new { error = "Could not move quiz to that collection." });
        }

        return Json(new { message = "Quiz moved." });
    }

    [HttpGet]
    public async Task<IActionResult> Settings(Guid? id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        Quiz? selectedQuiz = null;
        if (id.HasValue)
        {
            selectedQuiz = await _quizService.GetQuizByIdAsync(id.Value, userId, cancellationToken: cancellationToken);
            if (selectedQuiz == null)
                return RedirectToAction(nameof(Index));
        }

        var availableWordCount = selectedQuiz == null
            ? 0
            : await _quizService.GetAvailableWordCountAsync(selectedQuiz.Id, cancellationToken: cancellationToken);
        var availableSentenceCount = selectedQuiz == null
            ? 0
            : await _quizService.GetAvailableSentenceCountAsync(selectedQuiz.Id, cancellationToken: cancellationToken);
        IReadOnlyList<Word> words = selectedQuiz == null
            ? []
            : await _wordService.GetWordsAsync(selectedQuiz.Id, cancellationToken: cancellationToken);

        var ankiCollections = selectedQuiz is null
            ? []
            : (await _ankiCollections.ListAsync(userId, cancellationToken))
                .Where(collection => string.Equals(collection.SourceLanguage, selectedQuiz.SourceLanguage, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(collection.TargetLanguage, selectedQuiz.TargetLanguage, StringComparison.OrdinalIgnoreCase))
                .ToList();
        return View(new QuizSettingsViewModel
        {
            SelectedQuiz = selectedQuiz is null ? null : QuizCard.From(selectedQuiz),
            AvailableWordCount = availableWordCount,
            AvailableSentenceCount = availableSentenceCount,
            SelectedWordCount = Math.Min(Math.Max(availableWordCount, 1), 20),
            Words = words.Select(WordRow.From).ToList(),
            AnkiCollections = ankiCollections,
        });
    }

    [HttpPost]
    public async Task<IActionResult> Start(QuizSessionSettings settings, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        if (settings == null || !ModelState.IsValid)
            return RedirectToAction(nameof(Settings));

        if (settings.QuizId.HasValue
            && !await _quizService.UserOwnsQuizAsync(settings.QuizId.Value, userId, cancellationToken: cancellationToken))
        {
            return RedirectToAction(nameof(Index));
        }

        var normalizedItemType = PracticeItemType.Normalize(settings.PracticeItemType);
        if (settings.QuizId.HasValue)
        {
            var selectedQuiz = await _quizService.GetQuizByIdAsync(settings.QuizId.Value, userId, cancellationToken: cancellationToken);
            if (selectedQuiz is null)
            {
                return RedirectToAction(nameof(Index));
            }
            if (QuizLanguageCatalog.IsFreestyle(selectedQuiz.TargetLanguage))
            {
                normalizedItemType = PracticeItemType.Words;
            }
            var availableItemCount = PracticeItemType.IsSentences(normalizedItemType)
                ? await _quizService.GetAvailableSentenceCountAsync(settings.QuizId.Value, cancellationToken: cancellationToken)
                : await _quizService.GetAvailableWordCountAsync(settings.QuizId.Value, cancellationToken: cancellationToken);

            if (availableItemCount == 0)
            {
                return RedirectToAction(nameof(Settings), new { id = settings.QuizId.Value });
            }
        }

        return settings.Mode switch
        {
            "flashcards" => RedirectToAction("Index", "FlashcardQuiz", new { id = settings.QuizId, wordCount = settings.WordCount, practiceDirection = PracticeDirection.Normalize(settings.PracticeDirection), practiceItemType = normalizedItemType, wordRangeStart = settings.WordRangeStart, wordRangeEnd = settings.WordRangeEnd, selectedWordIds = settings.SelectedWordIds }),
            "typing" => RedirectToAction("Index", "TypingQuiz", new { id = settings.QuizId, wordCount = settings.WordCount, practiceDirection = PracticeDirection.Normalize(settings.PracticeDirection), practiceItemType = normalizedItemType, wordRangeStart = settings.WordRangeStart, wordRangeEnd = settings.WordRangeEnd, selectedWordIds = settings.SelectedWordIds }),
            // "multiple-choice" mode is exposed in settings UI but not yet implemented; route back to settings.
            _ => RedirectToAction(nameof(Settings))
        };
    }

    [HttpGet]
    public IActionResult Flashcard(Guid? id, int wordCount = 20, string? practiceDirection = null, string? practiceItemType = null)
    {
        return RedirectToAction("Index", "FlashcardQuiz", new { id, wordCount, practiceDirection = PracticeDirection.Normalize(practiceDirection), practiceItemType = PracticeItemType.Normalize(practiceItemType) });
    }

    [HttpGet]
    public IActionResult Type(Guid? id, int wordCount = 20, string? practiceDirection = null, string? practiceItemType = null)
    {
        return RedirectToAction("Index", "TypingQuiz", new { id, wordCount, practiceDirection = PracticeDirection.Normalize(practiceDirection), practiceItemType = PracticeItemType.Normalize(practiceItemType) });
    }

    private async Task<QuizIndexViewModel> BuildQuizIndexViewModelAsync(string userId, string language, Collection? currentCollection, CancellationToken cancellationToken = default)
    {
        var quizzes = await _quizService.GetUserQuizzesAsync(userId, cancellationToken: cancellationToken);
        var collections = await _collectionService.GetCollectionsAsync(userId, language, cancellationToken: cancellationToken);
        Collection? parentCollection = null;

        if (currentCollection?.ParentCollectionId is Guid parentCollectionId)
        {
            parentCollection = collections.FirstOrDefault(collection => collection.Id == parentCollectionId);
        }

        return new QuizIndexViewModel
        {
            Quizzes = quizzes.Select(QuizCard.From).ToList(),
            Collections = collections.Select(CollectionCard.From).ToList(),
            CurrentCollection = currentCollection is null ? null : CollectionCard.From(currentCollection),
            ParentCollection = parentCollection is null ? null : CollectionCard.From(parentCollection),
            CurrentLanguage = language
        };
    }

    private IActionResult RedirectToLibrary(Guid? collectionId)
    {
        return collectionId.HasValue
            ? RedirectToAction(nameof(Collection), new { id = collectionId.Value })
            : RedirectToAction(nameof(Index));
    }
}
