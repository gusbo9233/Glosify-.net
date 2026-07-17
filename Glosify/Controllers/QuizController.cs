using Glosify.Filters;
using Glosify.Models;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Ai;
using Glosify.Services.Language;
using Glosify.Services.Words;
using Glosify.Services.CustomQuizzes;

namespace Glosify.Controllers;

[Authorize]
public class QuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly ICollectionService _collectionService;
    private readonly IWordService _wordService;
    private readonly IQuizRepairService _quizRepairService;
    private readonly IImageTextExtractionService _imageTextExtractionService;
    private readonly ILanguageContext _languageContext;
    private readonly ICustomQuizService _customQuizService;

    public QuizController(
        IQuizService quizService,
        ICollectionService collectionService,
        IWordService wordService,
        IQuizRepairService quizRepairService,
        IImageTextExtractionService imageTextExtractionService,
        ILanguageContext languageContext,
        ICustomQuizService customQuizService)
    {
        _quizService = quizService;
        _collectionService = collectionService;
        _wordService = wordService;
        _quizRepairService = quizRepairService;
        _imageTextExtractionService = imageTextExtractionService;
        _languageContext = languageContext;
        _customQuizService = customQuizService;
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

        return View(new QuizWorkspaceViewModel
        {
            SelectedQuiz = selectedQuiz,
            Words = words,
            CustomQuizzes = await _customQuizService.ListForQuizAsync(selectedQuiz.Id, cancellationToken: cancellationToken),
            Sentences = sentences.Select(s => new QuizSentenceViewModel
            {
                Text = s.Text,
                Translation = s.Translation,
                WordCount = s.WordCount
            }).ToList()
        });
    }

    [HttpPost]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> RepairWord(string id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _quizRepairService.RepairWordAsync(id, userId, cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound(new { error = "Word not found." }),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant }),
            _ => Json(new { message = $"Repaired {result.Word}." })
        };
    }

    [HttpPost]
    [AiServiceExceptionFilter]
    public async Task<IActionResult> RepairSentence(Guid quizId, string text, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "Choose a sentence to repair." });

        var result = await _quizRepairService.RepairSentenceAsync(quizId, text, userId, cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound(new { error = "Quiz not found." }),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant }),
            _ => Json(new { message = result.UpdatedCount == 1 ? "Sentence repaired." : $"Sentence repaired in {result.UpdatedCount} places." })
        };
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

        await _wordService.AddWordAsync(input.QuizId, input.Word, input.Translation, quiz.SourceLanguage, quiz.TargetLanguage, cancellationToken: cancellationToken);

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

        TempData[NotificationKeys.Quiz] = $"Deleted {deleted.Lemma}.";
        return RedirectToAction(nameof(Details), new { id = deleted.QuizId });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteQuiz(Guid id, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var deleted = await _quizService.DeleteQuizAsync(id, userId, cancellationToken: cancellationToken);
        if (deleted != null)
        {
            TempData[NotificationKeys.Quiz] = $"Deleted {deleted.Name}.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> SetQuizVisibility(Guid id, bool isPublic, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();

        var updated = await _quizService.SetQuizPublicAsync(id, userId, isPublic, cancellationToken: cancellationToken);
        TempData[NotificationKeys.Quiz] = updated
            ? isPublic ? "Quiz is now public." : "Quiz is now private."
            : "Could not update quiz visibility.";

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
            ? isPublic ? "Collection is now public." : "Collection is now private."
            : "Could not update collection visibility.";

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
            TempData[NotificationKeys.Quiz] = $"Created collection {collection.Name}.";
        }
        catch (InvalidOperationException ex)
        {
            TempData[NotificationKeys.Quiz] = ex.Message;
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

        return View(new QuizSettingsViewModel
        {
            SelectedQuiz = selectedQuiz,
            AvailableWordCount = availableWordCount,
            AvailableSentenceCount = availableSentenceCount,
            SelectedWordCount = Math.Min(Math.Max(availableWordCount, 1), 20),
            Words = words
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

        if (settings.QuizId.HasValue)
        {
            var normalizedItemType = PracticeItemType.Normalize(settings.PracticeItemType);
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
            "flashcards" => RedirectToAction("Index", "FlashcardQuiz", new { id = settings.QuizId, wordCount = settings.WordCount, practiceDirection = PracticeDirection.Normalize(settings.PracticeDirection), practiceItemType = PracticeItemType.Normalize(settings.PracticeItemType), wordRangeStart = settings.WordRangeStart, wordRangeEnd = settings.WordRangeEnd, selectedWordIds = settings.SelectedWordIds }),
            "typing" => RedirectToAction("Index", "TypingQuiz", new { id = settings.QuizId, wordCount = settings.WordCount, practiceDirection = PracticeDirection.Normalize(settings.PracticeDirection), practiceItemType = PracticeItemType.Normalize(settings.PracticeItemType), wordRangeStart = settings.WordRangeStart, wordRangeEnd = settings.WordRangeEnd, selectedWordIds = settings.SelectedWordIds }),
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
            Quizzes = quizzes,
            Collections = collections,
            CurrentCollection = currentCollection,
            ParentCollection = parentCollection,
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
