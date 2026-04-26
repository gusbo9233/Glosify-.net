using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services;
using System.Security.Claims;

namespace Glosify.Controllers
{
    [Authorize]
    public class QuizController : Controller
    {
        private readonly GlosifyContext _context;
        private readonly ILanguageContext _languageContext;

        public QuizController(GlosifyContext context, ILanguageContext languageContext)
        {
            _context = context;
            _languageContext = languageContext;
        }

        /// <summary>
        /// Display the quiz selector with available quizzes.
        /// </summary>
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var language = _languageContext.CurrentLanguage;
            if (language == null)
                return RedirectToAction("Index", "Languages");

            var quizzes = await _context.Quizzes
                .Where(q => q.UserId.ToString() == userId && q.TargetLanguage == language)
                .ToListAsync();

            return View("select-quiz", quizzes);
        }

        /// <summary>
        /// Display the selected quiz workspace.
        /// </summary>
        [HttpGet]
        public async Task<IActionResult> Details(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var selectedQuiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == id && q.UserId.ToString() == userId);

            if (selectedQuiz == null)
                return RedirectToAction("Index");

            var language = _languageContext.CurrentLanguage;
            if (language == null)
                return RedirectToAction("Index", "Languages");
            if (!string.Equals(selectedQuiz.TargetLanguage, language, StringComparison.OrdinalIgnoreCase))
                return RedirectToAction("Index");

            var words = await _context.Words
                .Where(w => w.QuizId == selectedQuiz.Id)
                .OrderBy(w => w.Lemma)
                .ToListAsync();

            return View("quiz-view", new QuizWorkspaceViewModel
            {
                SelectedQuiz = selectedQuiz,
                Words = words
            });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> AddWord(Guid quizId, string word, string translation)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId.ToString() == userId);

            if (quiz == null)
                return RedirectToAction("Index");

            if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrWhiteSpace(translation))
            {
                var wordDetailId = Guid.NewGuid().ToString("N");
                _context.WordDetails.Add(new WordDetail
                {
                    Id = wordDetailId,
                    QuizId = quizId,
                    Language = quiz.TargetLanguage
                });

                _context.Words.Add(new Word
                {
                    Id = Guid.NewGuid().ToString("N"),
                    QuizId = quizId,
                    Lemma = word.Trim(),
                    Translation = translation.Trim(),
                    WordDetailId = wordDetailId
                });

                await _context.SaveChangesAsync();
            }

            return RedirectToAction("Details", new { id = quizId });
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

            var quiz = new Quiz
            {
                Id = Guid.NewGuid(),
                Name = Name,
                UserId = Guid.Parse(userId),
                SourceLanguage = SourceLanguage,
                TargetLanguage = language,
                Language = language,
                CreatedAt = DateTimeOffset.UtcNow,
                ProcessingStatus = "Ready"
            };

            _context.Quizzes.Add(quiz);
            await _context.SaveChangesAsync();

            return RedirectToAction("Details", new { id = quiz.Id });
        }

        /// <summary>
        /// Display quiz settings/configuration before starting
        /// </summary>
        [HttpGet]
        public IActionResult Settings(Guid? id)
        {
            // If an ID is provided, you might load specific quiz settings
            // For now, this displays the settings form
            return View();
        }

        /// <summary>
        /// Start a quiz session with user-selected settings
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> Start(QuizSessionSettings settings)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            // Validate settings
            if (settings == null || settings.WordCount <= 0)
                return RedirectToAction("Settings");

            // Redirect to appropriate quiz type view based on quiz type
            return settings.QuizType switch
            {
                "flashcard" => RedirectToAction("Flashcard"),
                "typing" => RedirectToAction("Type"),
                "multiple-choice" => RedirectToAction("MultipleChoice"),
                _ => RedirectToAction("Settings")
            };
        }

        /// <summary>
        /// Display flashcard quiz interface
        /// </summary>
        [HttpGet]
        public IActionResult Flashcard()
        {
            return View();
        }

        /// <summary>
        /// Handle flashcard quiz answer submission
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SubmitFlashcard([FromBody] FlashcardAnswer answer)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Process the answer (mark as correct/incorrect, update statistics)
            // This would typically update user progress and statistics in the database

            return Ok(new { success = true });
        }

        /// <summary>
        /// Display typing quiz interface
        /// </summary>
        [HttpGet]
        public IActionResult Type()
        {
            return View();
        }

        /// <summary>
        /// Handle typing quiz answer submission
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SubmitTyping([FromBody] TypingAnswer answer)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Validate the typed answer against the correct answer
            var isCorrect = ValidateTypingAnswer(answer.UserAnswer, answer.CorrectAnswer);

            return Ok(new { success = true, isCorrect });
        }

        /// <summary>
        /// Display multiple choice quiz interface
        /// </summary>
        [HttpGet]
        public IActionResult MultipleChoice()
        {
            return View();
        }

        /// <summary>
        /// Handle multiple choice quiz answer submission
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> SubmitMultipleChoice([FromBody] MultipleChoiceAnswer answer)
        {
            if (!ModelState.IsValid)
                return BadRequest(ModelState);

            // Check if the selected option is correct
            var isCorrect = answer.SelectedOption == answer.CorrectOption;

            return Ok(new { success = true, isCorrect });
        }

        /// <summary>
        /// End a quiz session and display results
        /// </summary>
        [HttpPost]
        public async Task<IActionResult> EndQuiz([FromBody] QuizSessionResult result)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            // Save quiz results to database
            // Update user statistics
            // Return results view or JSON

            return Ok(new { success = true, message = "Quiz session completed" });
        }

        private bool ValidateTypingAnswer(string userAnswer, string correctAnswer)
        {
            // Case-insensitive comparison with trimming
            return userAnswer.Trim().Equals(correctAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }

    // DTO Classes for API requests
    public class QuizSessionSettings
    {
        public int WordCount { get; set; }
        public string QuizType { get; set; } // flashcard, typing, multiple-choice
        public string? Language { get; set; }
        public int? Difficulty { get; set; }
    }

    public class FlashcardAnswer
    {
        public Guid WordId { get; set; }
        public bool IsKnown { get; set; }
        public int? Difficulty { get; set; }
    }

    public class TypingAnswer
    {
        public Guid WordId { get; set; }
        public string UserAnswer { get; set; }
        public string CorrectAnswer { get; set; }
    }

    public class MultipleChoiceAnswer
    {
        public Guid QuestionId { get; set; }
        public int SelectedOption { get; set; }
        public int CorrectOption { get; set; }
    }

    public class QuizSessionResult
    {
        public Guid QuizId { get; set; }
        public int TotalQuestions { get; set; }
        public int CorrectAnswers { get; set; }
        public int IncorrectAnswers { get; set; }
        public TimeSpan Duration { get; set; }
    }
}
