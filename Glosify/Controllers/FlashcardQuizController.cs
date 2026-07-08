using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Glosify.Services.Classrooms;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;

namespace Glosify.Controllers;

[Authorize]
public class FlashcardQuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly IFlashcardSessionService _sessionService;
    private readonly IClassroomService _classroomService;
    private readonly IQuizAttemptService _attemptService;

    public FlashcardQuizController(
        IQuizService quizService,
        IWordService wordService,
        IFlashcardSessionService sessionService,
        IClassroomService classroomService,
        IQuizAttemptService attemptService)
    {
        _quizService = quizService;
        _wordService = wordService;
        _sessionService = sessionService;
        _classroomService = classroomService;
        _attemptService = attemptService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, int wordCount = 20, string? practiceDirection = null, string? practiceItemType = null, Guid? classroomId = null, int wordRangeStart = 0, int wordRangeEnd = 100, string? selectedWordIds = null, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        var wordIds = WordIdList.Parse(selectedWordIds);

        Quiz? selectedQuiz;
        if (classroomId.HasValue && id.HasValue)
        {
            // Classroom-shared quizzes are practicable by members who don't own them.
            try
            {
                selectedQuiz = await _classroomService.RequireSharedQuizAsync(classroomId.Value, id.Value, userId, cancellationToken);
            }
            catch (ClassroomAccessDeniedException)
            {
                return RedirectToAction("Index", "Classroom");
            }
        }
        else
        {
            selectedQuiz = await _quizService.FindQuizAsync(userId, id, cancellationToken: cancellationToken);
        }

        if (selectedQuiz == null)
            return View(FlashcardQuizViewModel.Empty());

        // Hand-picked word sets always start a fresh session rather than resuming
        // one matched only by count/range, since the exact word set can't be
        // expressed in the resumability key.
        var resumed = wordIds.Count > 0
            ? null
            : _sessionService.FindResumableSession(userId, selectedQuiz.Id, normalizedDirection, normalizedItemType, wordCount, wordRangeStart, wordRangeEnd);
        if (resumed != null)
        {
            if (classroomId.HasValue && resumed.ClassroomId == null)
            {
                resumed.ClassroomId = classroomId;
                _sessionService.SaveSession(resumed);
            }

            return View(BuildViewModel(resumed, selectedQuiz));
        }

        var cards = wordIds.Count > 0
            ? await _wordService.LoadCardsByIdsAsync(selectedQuiz.Id, wordIds, cancellationToken: cancellationToken)
            : PracticeItemType.IsSentences(normalizedItemType)
                ? await _wordService.LoadSentenceCardsAsync(selectedQuiz.Id, wordCount, wordRangeStart, wordRangeEnd, cancellationToken: cancellationToken)
                : await _wordService.LoadCardsAsync(selectedQuiz.Id, wordCount, wordRangeStart, wordRangeEnd, cancellationToken: cancellationToken);
        var cardData = cards.Select(c => new FlashcardCardData
        {
            Id = c.Id,
            Lemma = c.Lemma,
            Translation = c.Translation,
            ExampleSentence = c.ExampleSentence,
            ExampleTranslation = c.ExampleTranslation
        }).ToList();

        var session = _sessionService.StartSession(
            userId,
            selectedQuiz.Id,
            selectedQuiz.Name,
            selectedQuiz.SourceLanguage,
            selectedQuiz.TargetLanguage,
            wordIds.Count > 0 ? cardData.Count : wordCount,
            cardData,
            normalizedDirection,
            normalizedItemType,
            wordRangeStart,
            wordRangeEnd,
            selectedWordIds);
        session.ClassroomId = classroomId;
        _sessionService.SaveSession(session);

        return View(BuildViewModel(session, selectedQuiz));
    }

    [HttpPost]
    public IActionResult Reveal(string sessionId)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null)
            return RedirectToAction(nameof(Index));

        _sessionService.RevealAnswer(session);
        _sessionService.SaveSession(session);

        return FlashcardResponse(session);
    }

    [HttpPost]
    public async Task<IActionResult> Rate(string sessionId, string rating, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null)
            return RedirectToAction(nameof(Index));

        _sessionService.ApplyRating(session, rating);

        // Flag before persisting so a re-posted final rating can't double-record.
        var justCompleted = session.CurrentIndex >= session.Cards.Count && !session.AttemptRecorded;
        if (justCompleted)
        {
            session.AttemptRecorded = true;
        }

        _sessionService.SaveSession(session);

        if (justCompleted)
        {
            await _attemptService.RecordFlashcardAttemptAsync(session, cancellationToken);
        }

        return FlashcardResponse(session);
    }

    [HttpPost]
    public IActionResult Restart(Guid quizId, int wordCount, string? practiceDirection = null, string? practiceItemType = null, Guid? classroomId = null, int wordRangeStart = 0, int wordRangeEnd = 100, string? selectedWordIds = null)
    {
        _sessionService.ResetSession(User.GetUserId(), quizId, practiceDirection, practiceItemType, wordCount, wordRangeStart, wordRangeEnd);
        return RedirectToAction(nameof(Index), new { id = quizId, wordCount, practiceDirection = PracticeDirection.Normalize(practiceDirection), practiceItemType = PracticeItemType.Normalize(practiceItemType), classroomId, wordRangeStart, wordRangeEnd, selectedWordIds });
    }

    private IActionResult FlashcardResponse(FlashcardSessionData session)
    {
        var quiz = new Quiz
        {
            Id = session.QuizId,
            Name = session.QuizName,
            SourceLanguage = session.SourceLanguage,
            TargetLanguage = session.TargetLanguage,
            Language = session.TargetLanguage,
            ProcessingStatus = "Ready"
        };
        var model = BuildViewModel(session, quiz);
        return Request.Headers.XRequestedWith == "XMLHttpRequest"
            ? PartialView("_FlashcardSession", model)
            : View("Index", model);
    }

    private static FlashcardQuizViewModel BuildViewModel(FlashcardSessionData session, Quiz quiz)
    {
        var totalCards = session.Cards.Count;
        var completedCards = Math.Min(session.CurrentIndex, totalCards);
        var currentCardData = session.CurrentIndex < totalCards ? session.Cards[session.CurrentIndex] : null;
        var currentCard = currentCardData == null ? null : new FlashcardWordViewModel
        {
            Id = currentCardData.Id,
            Lemma = currentCardData.Lemma,
            Translation = currentCardData.Translation,
            Prompt = currentCardData.Prompt,
            Answer = currentCardData.Answer,
            ExampleSentence = currentCardData.ExampleSentence,
            ExampleTranslation = currentCardData.ExampleTranslation
        };
        var totalAnswered = session.RememberedCount + session.AgainCount;

        return new FlashcardQuizViewModel
        {
            SelectedQuiz = quiz,
            CurrentCard = currentCard,
            SessionId = session.SessionId,
            QuizId = session.QuizId,
            CurrentIndex = session.CurrentIndex,
            CurrentCardNumber = currentCard == null ? totalCards : session.CurrentIndex + 1,
            TotalCards = totalCards,
            CompletedCards = completedCards,
            RememberedCount = session.RememberedCount,
            AgainCount = session.AgainCount,
            SkippedCount = session.SkippedCount,
            WordCount = session.WordCount,
            WordRangeStart = session.WordRangeStart,
            WordRangeEnd = session.WordRangeEnd,
            SelectedWordIds = session.SelectedWordIds,
            PracticeDirection = session.PracticeDirection,
            PromptLanguage = session.PromptLanguage,
            AnswerLanguage = session.AnswerLanguage,
            DirectionLabel = PracticeDirection.Label(session.PracticeDirection, session.SourceLanguage, session.TargetLanguage),
            PracticeItemType = session.PracticeItemType,
            ItemSingularLabel = PracticeItemType.SingularLabel(session.PracticeItemType),
            ItemPluralLabel = PracticeItemType.PluralLabel(session.PracticeItemType),
            CardLabel = PracticeItemType.CardLabel(session.PracticeItemType),
            IsAnswerRevealed = session.IsAnswerRevealed,
            IsComplete = totalCards > 0 && currentCard == null,
            ClassroomId = session.ClassroomId,
            ScorePercent = totalAnswered == 0 ? 0 : (int)Math.Round(session.RememberedCount * 100d / totalAnswered),
            ProgressPercent = totalCards == 0 ? 0 : (int)Math.Round(completedCards * 100d / totalCards)
        };
    }

    [HttpPost]
    public IActionResult RestartAgain(string sessionId)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null || session.AgainCards.Count == 0)
        {
            return RedirectToAction(nameof(Index));
        }

        var restarted = _sessionService.StartSession(
            userId,
            session.QuizId,
            session.QuizName,
            session.SourceLanguage,
            session.TargetLanguage,
            session.AgainCards.Count,
            session.AgainCards,
            session.PracticeDirection,
            session.PracticeItemType);

        restarted.ClassroomId = session.ClassroomId;
        _sessionService.SaveSession(restarted);
        return FlashcardResponse(restarted);
    }
}
