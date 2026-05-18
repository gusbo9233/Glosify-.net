using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.Controllers;

[Authorize]
public class FlashcardQuizController : Controller
{
    private readonly IQuizService _quizService;
    private readonly IWordService _wordService;
    private readonly IFlashcardSessionService _sessionService;

    public FlashcardQuizController(
        IQuizService quizService,
        IWordService wordService,
        IFlashcardSessionService sessionService)
    {
        _quizService = quizService;
        _wordService = wordService;
        _sessionService = sessionService;
    }

    [HttpGet]
    public async Task<IActionResult> Index(Guid? id, int wordCount = 20)
    {
        var userId = User.GetUserId();

        var selectedQuiz = await _quizService.FindQuizAsync(userId, id);
        if (selectedQuiz == null)
            return View(FlashcardQuizViewModel.Empty());

        var cards = await _wordService.LoadCardsAsync(selectedQuiz.Id, wordCount);
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
            wordCount,
            cardData);
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
    public IActionResult Rate(string sessionId, string rating)
    {
        var userId = User.GetUserId();
        var session = _sessionService.FindSession(sessionId, userId);
        if (session == null)
            return RedirectToAction(nameof(Index));

        _sessionService.ApplyRating(session, rating);
        _sessionService.SaveSession(session);

        return FlashcardResponse(session);
    }

    [HttpPost]
    public IActionResult Restart(Guid quizId, int wordCount)
    {
        return RedirectToAction(nameof(Index), new { id = quizId, wordCount });
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
            IsAnswerRevealed = session.IsAnswerRevealed,
            IsComplete = totalCards > 0 && currentCard == null,
            ScorePercent = totalAnswered == 0 ? 0 : (int)Math.Round(session.RememberedCount * 100d / totalAnswered),
            ProgressPercent = totalCards == 0 ? 0 : (int)Math.Round(completedCards * 100d / totalCards)
        };
    }
}
