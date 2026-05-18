using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class QuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly IQuizRepairService _quizRepairService;
    private readonly IGeneratedVocabularyService _generatedVocabularyService;
    private readonly IImageTextExtractionService _imageTextExtractionService;
    private readonly ILanguageContext _languageContext;
    private readonly ILogger<QuizController> _logger;

    public QuizController(
        IQuizService quizService,
        IWordService wordService,
        IQuizRepairService quizRepairService,
        IGeneratedVocabularyService generatedVocabularyService,
        IImageTextExtractionService imageTextExtractionService,
        ILanguageContext languageContext,
        ILogger<QuizController> logger)
    {
        _quizService = quizService;
        _wordService = wordService;
        _quizRepairService = quizRepairService;
        _generatedVocabularyService = generatedVocabularyService;
        _imageTextExtractionService = imageTextExtractionService;
        _languageContext = languageContext;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var userId = User.GetUserId();
        if (_languageContext.CurrentLanguage == null)
            return RedirectToAction("Index", "Languages");

        var quizzes = await _quizService.GetUserQuizzesAsync(userId);
        return View(quizzes);
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var userId = User.GetUserId();

        var selectedQuiz = await _quizService.GetQuizByIdAsync(id, userId);
        if (selectedQuiz == null)
            return RedirectToAction(nameof(Index));

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");
        if (!string.Equals(selectedQuiz.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction(nameof(Index));

        var words = await _wordService.GetWordsAsync(selectedQuiz.Id);
        var enriched = await _wordService.GetEnrichedWordDetailIdsAsync(selectedQuiz.Id);
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id);

        return View(new QuizWorkspaceViewModel
        {
            SelectedQuiz = selectedQuiz,
            Words = words,
            EnrichedWordDetailIds = enriched,
            Sentences = sentences.Select(s => new QuizSentenceViewModel
            {
                Text = s.Text,
                Translation = s.Translation,
                WordCount = s.WordCount
            }).ToList()
        });
    }

    [HttpPost]
    public async Task<IActionResult> RepairQuiz(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _quizRepairService.RepairQuizAsync(id, userId, cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound(new { error = "Quiz not found." }),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant }),
            _ => Json(new { message = "Quiz repaired." })
        };
    }

    [HttpPost]
    public async Task<IActionResult> RepairWord(string id, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();
        var result = await _quizRepairService.RepairWordAsync(id, userId, cancellationToken);
        return result.Status switch
        {
            QuizRepairStatus.NotFound => NotFound(new { error = "Word not found." }),
            QuizRepairStatus.LlmUnavailable => StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant }),
            _ => Json(new { message = $"Repaired {result.Lemma}." })
        };
    }

    [HttpPost]
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
            _ => Json(new { message = result.UpdatedCount == 1 ? "Sentence repaired." : $"Sentence repaired in {result.UpdatedCount} word details." })
        };
    }

    [HttpPost]
    public async Task<IActionResult> AddWord(AddWordInput input)
    {
        var userId = User.GetUserId();

        if (!ModelState.IsValid)
        {
            TempData["QuizMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Details), new { id = input.QuizId });
        }

        var quiz = await _quizService.GetQuizByIdAsync(input.QuizId, userId);
        if (quiz == null)
            return RedirectToAction(nameof(Index));

        await _wordService.AddWordAsync(input.QuizId, input.Word, input.Translation, quiz.SourceLanguage, quiz.TargetLanguage);

        return RedirectToAction(nameof(Details), new { id = input.QuizId });
    }

    [HttpPost]
    public async Task<IActionResult> GenerateWords(GenerateWordsInput model)
    {
        var wantsJson = WantsJsonResponse();
        var userId = User.GetUserId();

        if (!ModelState.IsValid)
        {
            var error = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            if (wantsJson)
                return BadRequest(new { error });
            TempData["AiError"] = error;
            return RedirectToAction(nameof(Details), new { id = model.QuizId });
        }

        GeneratedVocabularyResult result;
        try
        {
            result = await _generatedVocabularyService.GenerateAndAddWordsAsync(model.QuizId, userId, model.Input);
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex) || ServiceWarmupMessage.IsLlmWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted vocabulary generation for quiz {QuizId}", model.QuizId);
            var error = ServiceWarmupMessage.Dependencies;
            if (wantsJson)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error });
            TempData["AiError"] = error;
            return RedirectToAction(nameof(Details), new { id = model.QuizId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected vocabulary generation failure for quiz {QuizId}", model.QuizId);
            var error = "Something went wrong while generating words. Please try again in a moment.";
            if (wantsJson)
                return StatusCode(StatusCodes.Status500InternalServerError, new { error });
            TempData["AiError"] = error;
            return RedirectToAction(nameof(Details), new { id = model.QuizId });
        }

        if (result.Error != null)
        {
            if (wantsJson)
                return UnprocessableEntity(new { error = result.Error });
            TempData["AiError"] = result.Error;
        }
        else if (result.Message != null)
        {
            if (wantsJson)
                return Json(new { message = result.Message, addedCount = result.AddedCount });
            TempData["AiMessage"] = result.Message;
        }

        if (wantsJson)
            return Json(new { message = "Generation finished.", addedCount = result.AddedCount });

        return RedirectToAction(nameof(Details), new { id = model.QuizId });
    }

    [HttpPost]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> ExtractTextFromImage(Guid quizId, IFormFile? image, CancellationToken cancellationToken)
    {
        var userId = User.GetUserId();

        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
        if (quiz == null)
            return NotFound(new { error = "Quiz not found." });

        if (image == null || image.Length == 0)
            return BadRequest(new { error = "Take or choose a photo first." });

        if (image.Length > 8 * 1024 * 1024)
            return BadRequest(new { error = "Choose an image under 8 MB." });

        if (!image.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            return BadRequest(new { error = "Choose an image file." });

        try
        {
            await using var stream = image.OpenReadStream();
            var text = await _imageTextExtractionService.ExtractTextAsync(
                stream,
                image.ContentType,
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(text))
                return UnprocessableEntity(new { error = "No readable text was found in that image." });

            return Json(new { text });
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsLlmWarmupFailure(ex) || ex is HttpRequestException)
        {
            _logger.LogWarning(ex, "Image text extraction failed for quiz {QuizId}", quizId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.LlmAssistant });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteWord(string id)
    {
        var userId = User.GetUserId();

        var deleted = await _wordService.DeleteWordAsync(id, userId);
        if (deleted == null)
            return RedirectToAction(nameof(Index));

        TempData["QuizMessage"] = $"Deleted {deleted.Lemma}.";
        return RedirectToAction(nameof(Details), new { id = deleted.QuizId });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteQuiz(Guid id)
    {
        var userId = User.GetUserId();

        var deleted = await _quizService.DeleteQuizAsync(id, userId);
        if (deleted != null)
        {
            TempData["QuizMessage"] = $"Deleted {deleted.Name}.";
        }

        return RedirectToAction(nameof(Index));
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateQuizInput input)
    {
        var userId = User.GetUserId();

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        if (!ModelState.IsValid)
        {
            TempData["QuizMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction(nameof(Index));
        }

        var quiz = await _quizService.CreateQuizAsync(input.Name, input.SourceLanguage, language, userId);
        return RedirectToAction(nameof(Details), new { id = quiz.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Settings(Guid? id)
    {
        var userId = User.GetUserId();

        Quiz? selectedQuiz = null;
        if (id.HasValue)
        {
            selectedQuiz = await _quizService.GetQuizByIdAsync(id.Value, userId);
            if (selectedQuiz == null)
                return RedirectToAction(nameof(Index));
        }

        var availableWordCount = selectedQuiz == null
            ? 0
            : await _quizService.GetAvailableWordCountAsync(selectedQuiz.Id);

        return View(new QuizSettingsViewModel
        {
            SelectedQuiz = selectedQuiz,
            AvailableWordCount = availableWordCount,
            SelectedWordCount = Math.Min(Math.Max(availableWordCount, 1), 20)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Start(QuizSessionSettings settings)
    {
        var userId = User.GetUserId();

        if (settings == null || !ModelState.IsValid)
            return RedirectToAction(nameof(Settings));

        if (settings.QuizId.HasValue
            && !await _quizService.UserOwnsQuizAsync(settings.QuizId.Value, userId))
        {
            return RedirectToAction(nameof(Index));
        }

        return settings.Mode switch
        {
            "flashcards" => RedirectToAction("Index", "FlashcardQuiz", new { id = settings.QuizId, wordCount = settings.WordCount }),
            "typing" => RedirectToAction("Index", "TypingQuiz", new { id = settings.QuizId, wordCount = settings.WordCount }),
            // "multiple-choice" mode is exposed in settings UI but not yet implemented; route back to settings.
            _ => RedirectToAction(nameof(Settings))
        };
    }

    [HttpGet]
    public IActionResult Flashcard(Guid? id, int wordCount = 20)
    {
        return RedirectToAction("Index", "FlashcardQuiz", new { id, wordCount });
    }

    [HttpGet]
    public IActionResult Type(Guid? id, int wordCount = 20)
    {
        return RedirectToAction("Index", "TypingQuiz", new { id, wordCount });
    }

    private bool WantsJsonResponse()
    {
        return Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }
}
