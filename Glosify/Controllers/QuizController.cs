using System.Security.Claims;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Controllers;

[Authorize]
public class QuizController : Controller
{
    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly IGeneratedVocabularyService _generatedVocabularyService;
    private readonly IQuizServerVocabularyGenerationService _quizServerVocabularyGenerationService;
    private readonly IImageTextExtractionService _imageTextExtractionService;
    private readonly ILanguageContext _languageContext;
    private readonly ILogger<QuizController> _logger;

    public QuizController(
        GlosifyContext context,
        IQuizService quizService,
        IWordService wordService,
        IGeneratedVocabularyService generatedVocabularyService,
        IQuizServerVocabularyGenerationService quizServerVocabularyGenerationService,
        IImageTextExtractionService imageTextExtractionService,
        ILanguageContext languageContext,
        ILogger<QuizController> logger)
    {
        _context = context;
        _quizService = quizService;
        _wordService = wordService;
        _generatedVocabularyService = generatedVocabularyService;
        _quizServerVocabularyGenerationService = quizServerVocabularyGenerationService;
        _imageTextExtractionService = imageTextExtractionService;
        _languageContext = languageContext;
        _logger = logger;
    }

    public async Task<IActionResult> Index()
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        var quizzes = await _quizService.GetUserQuizzesAsync(userId);
        return View("select-quiz", quizzes);
    }

    [HttpGet]
    public async Task<IActionResult> Details(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var selectedQuiz = await _quizService.GetQuizByIdAsync(id, userId);
        if (selectedQuiz == null)
            return RedirectToAction("Index");

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");
        if (!string.Equals(selectedQuiz.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
            return RedirectToAction("Index");

        var words = await _wordService.GetWordsAsync(selectedQuiz.Id);
        var enriched = await _wordService.GetEnrichedWordDetailIdsAsync(selectedQuiz.Id);
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id);

        return View("quiz-view", new QuizWorkspaceViewModel
        {
            SelectedQuiz = selectedQuiz,
            Words = words,
            EnrichedWordDetailIds = enriched,
            Sentences = sentences
        });
    }

    [HttpPost]
    public async Task<IActionResult> RepairQuiz(Guid id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Sign in before repairing a quiz." });

        var repairData = await BuildRepairQuizDataAsync(id, userId);
        if (repairData == null)
            return NotFound(new { error = "Quiz not found." });

        var result = await _quizServerVocabularyGenerationService.RepairQuizAsync(repairData, cancellationToken);
        if (result?.QuizData == null)
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.QuizServer });

        await ApplyRepairedQuizAsync(id, result.QuizData);
        return Json(new { message = "Quiz repaired." });
    }

    [HttpPost]
    public async Task<IActionResult> RepairWord(string id, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Sign in before repairing a word." });

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == id, cancellationToken);
        if (word == null)
            return NotFound(new { error = "Word not found." });

        var repairData = await BuildRepairQuizDataAsync(word.QuizId, userId);
        if (repairData == null)
            return NotFound(new { error = "Quiz not found." });

        var result = await _quizServerVocabularyGenerationService.RepairWordAsync(repairData, id, cancellationToken);
        if (result?.Word == null || string.IsNullOrWhiteSpace(result.Word.Id))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.QuizServer });

        await ApplyRepairedWordAsync(result);
        return Json(new { message = $"Repaired {word.Lemma}." });
    }

    [HttpPost]
    public async Task<IActionResult> RepairSentence(Guid quizId, string text, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Sign in before repairing a sentence." });

        if (string.IsNullOrWhiteSpace(text))
            return BadRequest(new { error = "Choose a sentence to repair." });

        var repairData = await BuildRepairQuizDataAsync(quizId, userId);
        if (repairData == null)
            return NotFound(new { error = "Quiz not found." });

        var result = await _quizServerVocabularyGenerationService.RepairSentenceAsync(repairData, text, cancellationToken);
        if (result?.Sentence == null || string.IsNullOrWhiteSpace(result.Sentence.Text))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.QuizServer });

        var updatedCount = await ApplyRepairedSentenceAsync(quizId, text, result.Sentence, cancellationToken);
        return Json(new { message = updatedCount == 1 ? "Sentence repaired." : $"Sentence repaired in {updatedCount} word details." });
    }

    [HttpPost]
    public async Task<IActionResult> AddWord(AddWordInput input)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        if (!ModelState.IsValid)
        {
            TempData["QuizMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction("Details", new { id = input.QuizId });
        }

        var quiz = await _quizService.GetQuizByIdAsync(input.QuizId, userId);
        if (quiz == null)
            return RedirectToAction("Index");

        await _wordService.AddWordAsync(input.QuizId, input.Word, input.Translation, quiz.SourceLanguage, quiz.TargetLanguage);

        return RedirectToAction("Details", new { id = input.QuizId });
    }

    [HttpPost]
    public async Task<IActionResult> GenerateWords(GenerateWordsInput model)
    {
        var wantsJson = WantsJsonResponse();
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
        {
            if (wantsJson)
                return Unauthorized(new { error = "Sign in before generating words." });
            return RedirectToAction("Login", "Account");
        }

        if (!ModelState.IsValid)
        {
            var error = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            if (wantsJson)
                return BadRequest(new { error });
            TempData["AiError"] = error;
            return RedirectToAction("Details", new { id = model.QuizId });
        }

        GeneratedVocabularyResult result;
        try
        {
            result = await _generatedVocabularyService.GenerateAndAddWordsAsync(model.QuizId, userId, model.Input, model.AiProvider);
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex) || ServiceWarmupMessage.IsQuizServerWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Dependency warm-up interrupted vocabulary generation for quiz {QuizId}", model.QuizId);
            var error = ServiceWarmupMessage.Dependencies;
            if (wantsJson)
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error });
            TempData["AiError"] = error;
            return RedirectToAction("Details", new { id = model.QuizId });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected vocabulary generation failure for quiz {QuizId}", model.QuizId);
            var error = "Something went wrong while generating words. Please try again in a moment.";
            if (wantsJson)
                return StatusCode(StatusCodes.Status500InternalServerError, new { error });
            TempData["AiError"] = error;
            return RedirectToAction("Details", new { id = model.QuizId });
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

        return RedirectToAction("Details", new { id = model.QuizId });
    }

    private bool WantsJsonResponse()
    {
        return Request.Headers.Accept.Any(value => value?.Contains("application/json", StringComparison.OrdinalIgnoreCase) == true)
            || string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);
    }

    private async Task<QuizServerRepairQuizData?> BuildRepairQuizDataAsync(Guid quizId, string userId)
    {
        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
        if (quiz == null)
        {
            return null;
        }

        var rows = await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(row => row.Word.Lemma)
            .ToListAsync();

        var details = rows
            .Select(row => row.Detail)
            .OfType<WordDetail>()
            .GroupBy(detail => detail.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var sentences = details
            .Where(detail => !string.IsNullOrWhiteSpace(detail.ExampleSentence))
            .GroupBy(detail => detail.ExampleSentence.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select((group, index) => new QuizServerRepairSentence
            {
                Id = $"s{index + 1:000}",
                Text = group.Key,
                Translation = group
                    .Select(detail => detail.ExampleSentenceTranslation.Trim())
                    .FirstOrDefault(translation => !string.IsNullOrWhiteSpace(translation)) ?? string.Empty,
                QuizId = quiz.Id.ToString()
            })
            .ToList();

        return new QuizServerRepairQuizData
        {
            Quiz = new QuizServerRepairQuiz
            {
                Id = quiz.Id.ToString(),
                Name = quiz.Name,
                SourceLanguage = quiz.SourceLanguage,
                TargetLanguage = quiz.TargetLanguage,
                Language = string.IsNullOrWhiteSpace(quiz.Language)
                    ? quiz.TargetLanguage.ToLowerInvariant()
                    : quiz.Language.ToLowerInvariant(),
                ProcessingStatus = quiz.ProcessingStatus,
                ProcessingMessage = quiz.ProcessingMessage
            },
            Words = rows
                .Select(row => new QuizServerRepairWord
                {
                    Id = row.Word.Id,
                    Lemma = row.Word.Lemma,
                    Translation = row.Word.Translation,
                    WordDetailId = row.Word.WordDetailId,
                    QuizId = row.Word.QuizId.ToString()
                })
                .ToList(),
            WordDetails = details
                .Select(ToRepairWordDetail)
                .ToList(),
            Sentences = sentences
        };
    }

    private async Task ApplyRepairedQuizAsync(Guid quizId, QuizServerRepairQuizData repaired)
    {
        var wordsById = await _context.Words
            .Where(word => word.QuizId == quizId)
            .ToDictionaryAsync(word => word.Id);
        var detailsById = await _context.WordDetails
            .Where(detail => wordsById.Values.Select(word => word.WordDetailId).Contains(detail.Id))
            .ToDictionaryAsync(detail => detail.Id);

        foreach (var repairedWord in repaired.Words)
        {
            if (!wordsById.TryGetValue(repairedWord.Id, out var word))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(repairedWord.Lemma))
            {
                word.Lemma = repairedWord.Lemma.Trim();
            }
            if (!string.IsNullOrWhiteSpace(repairedWord.Translation))
            {
                word.Translation = repairedWord.Translation.Trim();
            }
        }

        foreach (var repairedDetail in repaired.WordDetails)
        {
            if (detailsById.TryGetValue(repairedDetail.Id, out var detail))
            {
                ApplyRepairedWordDetail(detail, repairedDetail);
            }
        }

        await _context.SaveChangesAsync();
    }

    private async Task ApplyRepairedWordAsync(QuizServerRepairWordResult result)
    {
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == result.Word.Id);
        if (word == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Word.Lemma))
        {
            word.Lemma = result.Word.Lemma.Trim();
        }
        if (!string.IsNullOrWhiteSpace(result.Word.Translation))
        {
            word.Translation = result.Word.Translation.Trim();
        }

        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == word.WordDetailId);
        if (detail != null)
        {
            result.WordDetail.Id = detail.Id;
            ApplyRepairedWordDetail(detail, result.WordDetail);
        }

        await _context.SaveChangesAsync();
    }

    private async Task<int> ApplyRepairedSentenceAsync(
        Guid quizId,
        string originalText,
        QuizServerRepairSentence repaired,
        CancellationToken cancellationToken)
    {
        var normalizedOriginal = VocabularyInputCleaner.CleanForVocabulary(originalText).Trim();
        var candidateDetails = await _context.Words
            .Where(word => word.QuizId == quizId)
            .Join(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (_, detail) => detail)
            .ToListAsync(cancellationToken);
        var details = candidateDetails
            .Where(detail => string.Equals(
                VocabularyInputCleaner.CleanForVocabulary(detail.ExampleSentence).Trim(),
                normalizedOriginal,
                StringComparison.OrdinalIgnoreCase))
            .GroupBy(detail => detail.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        foreach (var detail in details)
        {
            detail.ExampleSentence = repaired.Text.Trim();
            detail.ExampleSentenceTranslation = repaired.Translation.Trim();
            detail.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await _context.SaveChangesAsync(cancellationToken);
        return details.Count;
    }

    private static QuizServerRepairWordDetail ToRepairWordDetail(WordDetail detail)
    {
        return new QuizServerRepairWordDetail
        {
            Id = detail.Id,
            Properties = ParseJsonObject(detail.Properties),
            ExampleSentence = detail.ExampleSentence,
            ExampleSentenceTranslation = detail.ExampleSentenceTranslation,
            Explanation = detail.Explanation,
            Variants = ParseVariants(detail.Variants),
            Language = string.IsNullOrWhiteSpace(detail.Language)
                ? detail.TargetLanguage.ToLowerInvariant()
                : detail.Language.ToLowerInvariant()
        };
    }

    private static void ApplyRepairedWordDetail(WordDetail detail, QuizServerRepairWordDetail repaired)
    {
        if (repaired.Properties.Count > 0)
        {
            detail.Properties = JsonSerializer.Serialize(repaired.Properties);
        }
        if (repaired.Variants.Count > 0)
        {
            detail.Variants = JsonSerializer.Serialize(repaired.Variants);
        }
        if (!string.IsNullOrWhiteSpace(repaired.Explanation))
        {
            detail.Explanation = repaired.Explanation.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.ExampleSentence))
        {
            detail.ExampleSentence = repaired.ExampleSentence.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.ExampleSentenceTranslation))
        {
            detail.ExampleSentenceTranslation = repaired.ExampleSentenceTranslation.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.Language))
        {
            detail.Language = repaired.Language.Trim();
        }

        detail.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static Dictionary<string, JsonElement> ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<GeneratedWordVariant> ParseVariants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<GeneratedWordVariant>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    [HttpPost]
    [RequestSizeLimit(8 * 1024 * 1024)]
    public async Task<IActionResult> ExtractTextFromImage(Guid quizId, IFormFile? image, CancellationToken cancellationToken)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return Unauthorized(new { error = "Sign in before scanning text." });

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
        catch (Exception ex) when (ServiceWarmupMessage.IsQuizServerWarmupFailure(ex) || ex is HttpRequestException)
        {
            _logger.LogWarning(ex, "Image text extraction failed for quiz {QuizId}", quizId);
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { error = ServiceWarmupMessage.QuizServer });
        }
    }

    [HttpPost]
    public async Task<IActionResult> DeleteWord(string id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        if (string.IsNullOrWhiteSpace(id))
            return RedirectToAction("Index");

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == id);
        if (word == null)
            return RedirectToAction("Index");

        var quiz = await _quizService.GetQuizByIdAsync(word.QuizId, userId);
        if (quiz == null)
            return RedirectToAction("Index");

        _context.Words.Remove(word);
        await _context.SaveChangesAsync();

        TempData["QuizMessage"] = $"Deleted {word.Lemma}.";
        return RedirectToAction("Details", new { id = quiz.Id });
    }

    [HttpPost]
    public async Task<IActionResult> DeleteQuiz(Guid id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var success = await _quizService.DeleteQuizAsync(id, userId);

        if (success)
        {
            var quiz = await _context.Quizzes.AsNoTracking()
                .FirstOrDefaultAsync(q => q.Id == id);
            if (quiz != null)
            {
                TempData["QuizMessage"] = $"Deleted {quiz.Name}.";
            }
        }

        return RedirectToAction("Index");
    }

    [HttpPost]
    public async Task<IActionResult> Create(CreateQuizInput input)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        if (!ModelState.IsValid)
        {
            TempData["QuizMessage"] = string.Join(" ", ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage));
            return RedirectToAction("Index");
        }

        var quiz = await _quizService.CreateQuizAsync(input.Name, input.SourceLanguage, language, userId);
        return RedirectToAction("Details", new { id = quiz.Id });
    }

    [HttpGet]
    public async Task<IActionResult> Settings(Guid? id)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        Quiz? selectedQuiz = null;
        if (id.HasValue)
        {
            selectedQuiz = await _quizService.GetQuizByIdAsync(id.Value, userId);
            if (selectedQuiz == null)
                return RedirectToAction("Index");
        }

        var availableWordCount = selectedQuiz == null
            ? 0
            : await _quizService.GetAvailableWordCountAsync(selectedQuiz.Id);

        return View("start-quiz-settings", new QuizSettingsViewModel
        {
            SelectedQuiz = selectedQuiz,
            AvailableWordCount = availableWordCount,
            SelectedWordCount = Math.Min(Math.Max(availableWordCount, 1), 20)
        });
    }

    [HttpPost]
    public async Task<IActionResult> Start(QuizSessionSettings settings)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        if (settings == null || !ModelState.IsValid)
            return RedirectToAction("Settings");

        if (settings.QuizId.HasValue)
        {
            var ownsQuiz = await _context.Quizzes
                .AnyAsync(q => q.Id == settings.QuizId.Value && q.UserId == userId);

            if (!ownsQuiz)
                return RedirectToAction("Index");
        }

        return settings.Mode switch
        {
            "flashcards" => RedirectToAction("Index", "FlashcardQuiz", new { id = settings.QuizId, wordCount = settings.WordCount }),
            "typing" => RedirectToAction("Index", "TypingQuiz", new { id = settings.QuizId, wordCount = settings.WordCount }),
            // "multiple-choice" mode is exposed in settings UI but not yet implemented; route back to settings.
            _ => RedirectToAction("Settings")
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
}
