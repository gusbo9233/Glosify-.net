using System.Security.Claims;
using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class TypingQuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly ITypingQuizService _typingQuizService;
    private readonly ILanguageContext _languageContext;

    public TypingQuizController(
        IQuizService quizService,
        ITypingQuizService typingQuizService,
        ILanguageContext languageContext)
    {
        _quizService = quizService;
        _typingQuizService = typingQuizService;
        _languageContext = languageContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, int wordCount = 20)
    {
        var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrEmpty(userId))
            return RedirectToAction("Login", "Account");

        var selectedQuiz = await _quizService.FindQuizAsync(userId, id);
        if (selectedQuiz == null)
            return View("~/Views/Quiz/typewordquiz.cshtml", TypingQuizViewModel.Empty());

        var data = await _typingQuizService.GetQuizDataAsync(selectedQuiz.Id, wordCount);
        var showsUkrainianKeyboard = _languageContext.CurrentLanguage == "Ukrainian";

        return View("~/Views/Quiz/typewordquiz.cshtml", BuildViewModel(data, wordCount, showsUkrainianKeyboard));
    }

    [HttpPost]
    public IActionResult Submit([FromBody] TypingAnswer answer)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var isCorrect = _typingQuizService.CheckAnswer(answer.UserAnswer, answer.CorrectAnswer);

        return Ok(new { success = true, isCorrect });
    }

    private static TypingQuizViewModel BuildViewModel(TypingQuizData data, int wordCount, bool showsUkrainianKeyboard)
    {
        var words = data.Words.Select(w => new TypingQuizWordViewModel
        {
            Id = w.Id,
            Prompt = w.Prompt,
            Answer = w.Answer,
            ExampleSentence = w.ExampleSentence,
            ExampleTranslation = w.ExampleTranslation
        }).ToList();

        return new TypingQuizViewModel
        {
            SelectedQuiz = new Quiz
            {
                Id = data.QuizId,
                Name = data.QuizName,
                SourceLanguage = data.SourceLanguage,
                TargetLanguage = data.TargetLanguage,
                Language = data.TargetLanguage,
                ProcessingStatus = "Ready"
            },
            QuizId = data.QuizId,
            WordCount = Math.Clamp(wordCount, 1, 100),
            Words = words,
            ShowsUkrainianKeyboard = showsUkrainianKeyboard
        };
    }
}
