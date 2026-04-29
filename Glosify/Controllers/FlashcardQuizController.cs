using System.Security.Claims;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Controllers
{
    [Authorize]
    public class FlashcardQuizController : Controller
    {
        private static readonly TimeSpan SessionLifetime = TimeSpan.FromMinutes(45);

        private readonly GlosifyContext _context;
        private readonly ILanguageContext _languageContext;
        private readonly IMemoryCache _cache;

        public FlashcardQuizController(
            GlosifyContext context,
            ILanguageContext languageContext,
            IMemoryCache cache)
        {
            _context = context;
            _languageContext = languageContext;
            _cache = cache;
        }

        [HttpGet]
        public async Task<IActionResult> Index(Guid? id, int wordCount = 20)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var selectedQuiz = await FindQuizAsync(userId, id);
            if (selectedQuiz == null)
            {
                return View("~/Views/Quiz/flashcard-quiz.cshtml", FlashcardQuizViewModel.Empty());
            }

            var cards = await LoadCardsAsync(selectedQuiz.Id, wordCount);
            var session = FlashcardSessionState.Start(userId, selectedQuiz, Math.Clamp(wordCount, 1, 100), cards);
            SaveSession(session);

            return View("~/Views/Quiz/flashcard-quiz.cshtml", BuildViewModel(session));
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Reveal(string sessionId)
        {
            var session = FindSession(sessionId);
            if (session == null)
                return RedirectToAction("Index");

            session.IsAnswerRevealed = true;
            SaveSession(session);

            return FlashcardResponse(session);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Rate(string sessionId, string rating)
        {
            var session = FindSession(sessionId);
            if (session == null)
                return RedirectToAction("Index");

            ApplyRating(session, rating);
            SaveSession(session);

            return FlashcardResponse(session);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public IActionResult Restart(Guid quizId, int wordCount)
        {
            return RedirectToAction("Index", new { id = quizId, wordCount });
        }

        private async Task<Quiz?> FindQuizAsync(string userId, Guid? quizId)
        {
            var language = _languageContext.CurrentLanguage;
            var query = _context.Quizzes.Where(q => q.UserId.ToString() == userId);
            if (!string.IsNullOrWhiteSpace(language))
            {
                query = query.Where(q => q.TargetLanguage == language);
            }

            return quizId.HasValue
                ? await query.FirstOrDefaultAsync(q => q.Id == quizId.Value)
                : await query.OrderByDescending(q => q.CreatedAt).FirstOrDefaultAsync();
        }

        private async Task<List<FlashcardWordViewModel>> LoadCardsAsync(Guid quizId, int wordCount)
        {
            var take = Math.Clamp(wordCount, 1, 100);

            return await _context.Words
                .Where(word => word.QuizId == quizId)
                .GroupJoin(
                    _context.WordDetails.Where(detail => detail.QuizId == quizId),
                    word => word.WordDetailId,
                    detail => detail.Id,
                    (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
                .OrderBy(_ => Guid.NewGuid())
                .Take(take)
                .Select(item => new FlashcardWordViewModel
                {
                    Id = item.Word.Id,
                    Lemma = item.Word.Lemma,
                    Translation = item.Word.Translation,
                    ExampleSentence = item.Detail == null ? string.Empty : item.Detail.ExampleSentence,
                    ExampleTranslation = item.Detail == null ? string.Empty : item.Detail.Explanation
                })
                .ToListAsync();
        }

        private FlashcardSessionState? FindSession(string? sessionId)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrWhiteSpace(userId) || string.IsNullOrWhiteSpace(sessionId))
            {
                return null;
            }

            return _cache.TryGetValue(CacheKey(sessionId), out FlashcardSessionState? session)
                && session?.UserId == userId
                ? session
                : null;
        }

        private void SaveSession(FlashcardSessionState session)
        {
            _cache.Set(
                CacheKey(session.SessionId),
                session,
                new MemoryCacheEntryOptions { SlidingExpiration = SessionLifetime });
        }

        private IActionResult FlashcardResponse(FlashcardSessionState session)
        {
            var model = BuildViewModel(session);
            return Request.Headers.XRequestedWith == "XMLHttpRequest"
                ? PartialView("~/Views/Quiz/_FlashcardSession.cshtml", model)
                : View("~/Views/Quiz/flashcard-quiz.cshtml", model);
        }

        private static FlashcardQuizViewModel BuildViewModel(FlashcardSessionState session)
        {
            var totalCards = session.Cards.Count;
            var completedCards = Math.Min(session.CurrentIndex, totalCards);
            var currentCard = session.CurrentIndex < totalCards ? session.Cards[session.CurrentIndex] : null;
            var totalAnswered = session.RememberedCount + session.AgainCount;

            return new FlashcardQuizViewModel
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

        private static void ApplyRating(FlashcardSessionState session, string? rating)
        {
            if (session.CurrentIndex >= session.Cards.Count)
            {
                return;
            }

            switch (rating?.Trim().ToLowerInvariant())
            {
                case "again":
                    session.AgainCount++;
                    break;
                case "skip":
                    session.SkippedCount++;
                    break;
                default:
                    session.RememberedCount++;
                    break;
            }

            session.CurrentIndex++;
            session.IsAnswerRevealed = false;
        }

        private static string CacheKey(string sessionId) => $"flashcard-quiz:{sessionId}";

        private sealed class FlashcardSessionState
        {
            public string SessionId { get; set; } = string.Empty;
            public string UserId { get; set; } = string.Empty;
            public Guid QuizId { get; set; }
            public string QuizName { get; set; } = string.Empty;
            public string SourceLanguage { get; set; } = string.Empty;
            public string TargetLanguage { get; set; } = string.Empty;
            public int WordCount { get; set; }
            public int CurrentIndex { get; set; }
            public int RememberedCount { get; set; }
            public int AgainCount { get; set; }
            public int SkippedCount { get; set; }
            public bool IsAnswerRevealed { get; set; }
            public IReadOnlyList<FlashcardWordViewModel> Cards { get; set; } = [];

            public static FlashcardSessionState Start(
                string userId,
                Quiz quiz,
                int wordCount,
                IReadOnlyList<FlashcardWordViewModel> cards) => new()
                {
                    SessionId = Guid.NewGuid().ToString("N"),
                    UserId = userId,
                    QuizId = quiz.Id,
                    QuizName = quiz.Name,
                    SourceLanguage = quiz.SourceLanguage,
                    TargetLanguage = quiz.TargetLanguage,
                    WordCount = wordCount,
                    Cards = cards
                };
        }
    }
}
