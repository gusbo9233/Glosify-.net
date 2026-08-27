using Glosify.Extensions;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class FlashcardQuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly IFlashcardSessionService _sessionService;
    private readonly IQuizAttemptService _attemptService;

    public FlashcardQuizController(
        IQuizService quizService,
        IWordService wordService,
        IFlashcardSessionService sessionService,
        IQuizAttemptService attemptService)
    {
        _quizService = quizService;
        _wordService = wordService;
        _sessionService = sessionService;
        _attemptService = attemptService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, int wordCount = 20, string? practiceDirection = null, string? practiceItemType = null, int wordRangeStart = 0, int wordRangeEnd = 100, string? selectedWordIds = null, CancellationToken cancellationToken = default)
    {
        var userId = User.GetUserId();
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        var wordIds = WordIdList.Parse(selectedWordIds);

        var selectedQuiz = await _quizService.FindQuizAsync(userId, id, cancellationToken: cancellationToken);

        if (selectedQuiz == null)
            return View(FlashcardQuizViewModel.Empty());
        if (QuizLanguageCatalog.IsFreestyle(selectedQuiz.TargetLanguage))
        {
            normalizedItemType = PracticeItemType.Words;
        }

        // Hand-picked word sets always start a fresh session rather than resuming
        // one matched only by count/range, since the exact word set can't be
        // expressed in the resumability key.
        var resumed = wordIds.Count > 0
            ? null
            : _sessionService.FindResumableSession(userId, selectedQuiz.Id, normalizedDirection, normalizedItemType, wordCount, wordRangeStart, wordRangeEnd);
        if (resumed != null)
        {
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
    public IActionResult Restart(Guid quizId, int wordCount, string? practiceDirection = null, string? practiceItemType = null, int wordRangeStart = 0, int wordRangeEnd = 100, string? selectedWordIds = null)
    {
        _sessionService.ResetSession(User.GetUserId(), quizId, practiceDirection, practiceItemType, wordCount, wordRangeStart, wordRangeEnd);
        return RedirectToAction(nameof(Index), new { id = quizId, wordCount, practiceDirection = PracticeDirection.Normalize(practiceDirection), practiceItemType = PracticeItemType.Normalize(practiceItemType), wordRangeStart, wordRangeEnd, selectedWordIds });
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
        var isFreestyle = QuizLanguageCatalog.IsFreestyle(quiz.TargetLanguage);
        var totalCards = session.Cards.Count;
        var completedCards = Math.Min(session.CurrentIndex, totalCards);
        var currentCardData = session.CurrentIndex < totalCards ? session.Cards[session.CurrentIndex] : null;
        var currentCard = currentCardData == null ? null : new FlashcardWordViewModel
        {
            Prompt = currentCardData.Prompt,
            Answer = currentCardData.Answer,
            ExampleSentence = currentCardData.ExampleSentence,
            ExampleTranslation = currentCardData.ExampleTranslation
        };
        var totalAnswered = session.RememberedCount + session.AgainCount;

        return new FlashcardQuizViewModel
        {
            SelectedQuiz = QuizCard.From(quiz),
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
            DirectionLabel = isFreestyle
                ? (PracticeDirection.IsTargetToSource(session.PracticeDirection) ? "Answer → Prompt" : "Prompt → Answer")
                : PracticeDirection.Label(session.PracticeDirection, session.SourceLanguage, session.TargetLanguage),
            PracticeItemType = session.PracticeItemType,
            ItemSingularLabel = isFreestyle ? "item" : PracticeItemType.SingularLabel(session.PracticeItemType),
            ItemPluralLabel = isFreestyle ? "items" : PracticeItemType.PluralLabel(session.PracticeItemType),
            CardLabel = isFreestyle ? "Item" : PracticeItemType.CardLabel(session.PracticeItemType),
            IsAnswerRevealed = session.IsAnswerRevealed,
            IsComplete = totalCards > 0 && currentCard == null,
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

        _sessionService.SaveSession(restarted);
        return FlashcardResponse(restarted);
    }
}
