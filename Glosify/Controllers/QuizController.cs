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
    private readonly ILanguageContext _languageContext;

    public QuizController(
        GlosifyContext context,
        IQuizService quizService,
        IWordService wordService,
        IGeneratedVocabularyService generatedVocabularyService,
        ILanguageContext languageContext)
    {
        _context = context;
        _quizService = quizService;
        _wordService = wordService;
        _generatedVocabularyService = generatedVocabularyService;
        _languageContext = languageContext;
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
        var sentences = await _wordService.GetSentencesAsync(selectedQuiz.Id);

        return View("quiz-view", new QuizWorkspaceViewModel
        {
            SelectedQuiz = selectedQuiz,
            Words = words,
            Sentences = sentences
        });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> AddWord(Guid quizId, string word, string translation)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
        if (quiz == null)
            return RedirectToAction("Index");

        await _wordService.AddWordAsync(quizId, word, translation, quiz.SourceLanguage, quiz.TargetLanguage);

        return RedirectToAction("Details", new { id = quizId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> GenerateWords(Guid quizId, string input)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var result = await _generatedVocabularyService.GenerateAndAddWordsAsync(quizId, userId, input);

        if (result.Error != null)
        {
            TempData["AiError"] = result.Error;
        }
        else if (result.Message != null)
        {
            TempData["AiMessage"] = result.Message;
        }

        return RedirectToAction("Details", new { id = quizId });
    }

    [HttpPost]
    [ValidateAntiForgeryToken]
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
    [ValidateAntiForgeryToken]
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Create(string Name, string SourceLanguage, string TargetLanguage)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var language = _languageContext.CurrentLanguage;
        if (language == null)
            return RedirectToAction("Index", "Languages");

        var quiz = await _quizService.CreateQuizAsync(Name, SourceLanguage, language, userId);
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
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> Start(QuizSessionSettings settings)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        if (settings == null || settings.WordCount <= 0)
            return RedirectToAction("Settings");

        if (settings.QuizId.HasValue)
        {
            var ownsQuiz = await _context.Quizzes
                .AnyAsync(q => q.Id == settings.QuizId.Value && q.UserId.ToString() == userId);

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
