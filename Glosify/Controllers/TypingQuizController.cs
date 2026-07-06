using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;

namespace Glosify.Controllers;

[Authorize]
public class TypingQuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly ITypingQuizService _typingQuizService;
    private readonly ITypingSessionService _sessionService;
    private readonly ILanguageContext _languageContext;

    public TypingQuizController(
        IQuizService quizService,
        ITypingQuizService typingQuizService,
        ITypingSessionService sessionService,
        ILanguageContext languageContext)
    {
        _quizService = quizService;
        _typingQuizService = typingQuizService;
        _sessionService = sessionService;
        _languageContext = languageContext;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, int wordCount = 20, string? practiceDirection = null, string? practiceItemType = null, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);

        var selectedQuiz = await _quizService.FindQuizAsync(userId, id, cancellationToken: cancellationToken);
        if (selectedQuiz == null)
            return View(TypingQuizViewModel.Empty());

        var resumed = _sessionService.FindResumableSession(userId, selectedQuiz.Id, normalizedDirection, normalizedItemType, wordCount);
        if (resumed != null)
            return RedirectToAction(nameof(Session), new { sessionId = resumed.SessionId });

        var data = await _typingQuizService.GetQuizDataAsync(selectedQuiz.Id, wordCount, normalizedDirection, normalizedItemType);
        var session = _sessionService.StartSession(
            userId,
            data.QuizId,
            data.QuizName,
            data.SourceLanguage,
            data.TargetLanguage,
            wordCount,
            data.Words,
            data.PracticeDirection,
            data.PracticeItemType);
        _sessionService.SaveSession(session);

        return RedirectToAction(nameof(Session), new { sessionId = session.SessionId });
    }

    [HttpGet]
    public IActionResult Session(string sessionId)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null)
            return RedirectToAction("Index", "Quiz");

        var showsUkrainianKeyboard = string.Equals(session.AnswerLanguage, "Ukrainian", StringComparison.OrdinalIgnoreCase);
        return View("Index", BuildViewModel(session, showsUkrainianKeyboard));
    }

    [HttpPost]
    public IActionResult Submit([FromBody] TypingAnswer answer)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = User.GetUserId();
        var session = _sessionService.FindSession(answer.SessionId, userId);
        if (session == null)
            return NotFound(new { success = false, message = "Typing session expired." });

        var result = _sessionService.SubmitAnswer(session, answer.UserAnswer);
        _sessionService.SaveSession(session);

        return Ok(new
        {
            success = true,
            result.IsCorrect,
            result.CorrectAnswer,
            result.ExampleSentence,
            result.ExampleTranslation,
            session.CurrentIndex,
            currentWordNumber = session.CurrentIndex < session.Words.Count ? session.CurrentIndex + 1 : session.Words.Count,
            totalWords = session.Words.Count,
            completedWords = Math.Min(session.CurrentIndex, session.Words.Count),
            session.CorrectCount,
            session.IncorrectCount,
            isComplete = session.Words.Count > 0 && session.CurrentIndex >= session.Words.Count,
            scorePercent = GetScorePercent(session),
            progressPercent = GetProgressPercent(session),
            nextWord = result.NextWord == null
                ? null
                : new
                {
                    id = result.NextWord.Id,
                    prompt = result.NextWord.Prompt
                }
        });
    }

    [HttpPost]
    public IActionResult Restart(Guid quizId, int wordCount, string? practiceDirection = null, string? practiceItemType = null)
    {
        _sessionService.ResetSession(User.GetUserId(), quizId, practiceDirection, practiceItemType, wordCount);
        return RedirectToAction(nameof(Index), new { id = quizId, wordCount, practiceDirection = PracticeDirection.Normalize(practiceDirection), practiceItemType = PracticeItemType.Normalize(practiceItemType) });
    }

    [HttpPost]
    public IActionResult RestartIncorrect(string sessionId)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null || session.IncorrectWords.Count == 0)
        {
            return RedirectToAction(nameof(Index));
        }

        var restarted = _sessionService.StartSession(
            userId,
            session.QuizId,
            session.QuizName,
            session.SourceLanguage,
            session.TargetLanguage,
            session.IncorrectWords.Count,
            session.IncorrectWords,
            session.PracticeDirection,
            session.PracticeItemType);

        _sessionService.SaveSession(restarted);

        return RedirectToAction(nameof(Session), new { sessionId = restarted.SessionId });
    }

    private static TypingQuizViewModel BuildViewModel(TypingSessionData session, bool showsUkrainianKeyboard)
    {
        var totalWords = session.Words.Count;
        var completedWords = Math.Min(session.CurrentIndex, totalWords);
        var currentWordData = session.CurrentIndex < totalWords ? session.Words[session.CurrentIndex] : null;
        var currentWord = currentWordData == null ? null : new TypingQuizWordViewModel
        {
            Id = currentWordData.Id,
            Prompt = currentWordData.Prompt
        };

        return new TypingQuizViewModel
        {
            SelectedQuiz = new Quiz
            {
                Id = session.QuizId,
                Name = session.QuizName,
                SourceLanguage = session.SourceLanguage,
                TargetLanguage = session.TargetLanguage,
                Language = session.TargetLanguage,
                ProcessingStatus = "Ready"
            },
            CurrentWord = currentWord,
            SessionId = session.SessionId,
            QuizId = session.QuizId,
            CurrentWordNumber = currentWord == null ? totalWords : session.CurrentIndex + 1,
            TotalWords = totalWords,
            CompletedWords = completedWords,
            CorrectCount = session.CorrectCount,
            IncorrectCount = session.IncorrectCount,
            ScorePercent = GetScorePercent(session),
            ProgressPercent = GetProgressPercent(session),
            WordCount = session.WordCount,
            PracticeDirection = session.PracticeDirection,
            PromptLanguage = session.PromptLanguage,
            AnswerLanguage = session.AnswerLanguage,
            DirectionLabel = PracticeDirection.Label(session.PracticeDirection, session.SourceLanguage, session.TargetLanguage),
            PracticeItemType = session.PracticeItemType,
            ItemSingularLabel = PracticeItemType.SingularLabel(session.PracticeItemType),
            ItemPluralLabel = PracticeItemType.PluralLabel(session.PracticeItemType),
            CardLabel = PracticeItemType.CardLabel(session.PracticeItemType),
            ShowsUkrainianKeyboard = showsUkrainianKeyboard,
            IsComplete = totalWords > 0 && currentWord == null
        };
    }

    private static int GetScorePercent(TypingSessionData session)
    {
        var answered = session.CorrectCount + session.IncorrectCount;
        return answered == 0 ? 0 : (int)Math.Round(session.CorrectCount * 100d / answered);
    }

    private static int GetProgressPercent(TypingSessionData session)
    {
        return session.Words.Count == 0 ? 0 : (int)Math.Round(Math.Min(session.CurrentIndex, session.Words.Count) * 100d / session.Words.Count);
    }
}
