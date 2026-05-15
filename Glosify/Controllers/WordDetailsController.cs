using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services;

namespace Glosify.Controllers
{
    [Authorize]
    public class WordDetailsController : Controller
    {
        private readonly GlosifyContext _context;
        private readonly IWordDetailViewModelService _viewModelService;
        private readonly IWordDetailEnrichmentService _enrichmentService;

        public WordDetailsController(
            GlosifyContext context,
            IWordDetailViewModelService viewModelService,
            IWordDetailEnrichmentService enrichmentService)
        {
            _context = context;
            _viewModelService = viewModelService;
            _enrichmentService = enrichmentService;
        }

        // GET: WordDetails
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var wordDetails = await _context.WordDetails
                .Join(
                    _context.Words.Join(
                        _context.Quizzes.Where(q => q.UserId == userId),
                        word => word.QuizId,
                        quiz => quiz.Id,
                        (word, _) => word.WordDetailId),
                    wordDetail => wordDetail.Id,
                    wordDetailId => wordDetailId,
                    (wordDetail, _) => wordDetail)
                .Distinct()
                .ToListAsync();

            return View(wordDetails);
        }

        // GET: WordDetails/Details/5
        public async Task<IActionResult> Details(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var model = await _viewModelService.BuildAsync(id, userId);
            if (model == null)
            {
                return NotFound();
            }

            return View(model);
        }

        // GET: WordDetails/Create
        public IActionResult Create()
        {
            return View();
        }

        // POST: WordDetails/Generate/{id}
        // JSON endpoint used by the quiz-view bulk Generate button. Skips work for
        // already-enriched details (force:false) so re-runs are cheap.
        [HttpPost]
        public async Task<IActionResult> Generate(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
            {
                return Unauthorized();
            }

            var owned = await LoadOwnedWordDetailWithWordAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }

            var (detail, word, quiz) = owned.Value;
            var changed = await _enrichmentService.EnrichAsync(
                detail,
                word,
                quiz,
                string.IsNullOrWhiteSpace(detail.Word) ? word.Lemma : detail.Word,
                string.IsNullOrWhiteSpace(detail.TargetLanguage) ? quiz.TargetLanguage : detail.TargetLanguage);

            if (changed)
            {
                await _context.SaveChangesAsync();
            }

            var isEnriched = !string.IsNullOrWhiteSpace(detail.Explanation)
                && !string.IsNullOrWhiteSpace(detail.ExampleSentence)
                && detail.Properties != "{}"
                && detail.Variants != "[]";

            return Json(new { ok = true, lemma = word.Lemma, isEnriched });
        }

        [HttpPost]
        public async Task<IActionResult> Regenerate(string id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var owned = await LoadOwnedWordDetailWithWordAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }

            var changed = await _enrichmentService.EnrichAsync(
                owned.Value.Detail,
                owned.Value.Word,
                owned.Value.Quiz,
                string.IsNullOrWhiteSpace(owned.Value.Detail.Word) ? owned.Value.Word.Lemma : owned.Value.Detail.Word,
                string.IsNullOrWhiteSpace(owned.Value.Detail.TargetLanguage)
                    ? owned.Value.Quiz.TargetLanguage
                    : owned.Value.Detail.TargetLanguage,
                force: true);

            if (changed)
            {
                await _context.SaveChangesAsync();
                TempData["WordDetailMessage"] = "Word details regenerated.";
            }
            else
            {
                TempData["WordDetailMessage"] = "No regenerated details were returned.";
            }

            return RedirectToAction(nameof(Details), new { id });
        }

        // POST: WordDetails/Create
        // Word details are shared cache rows keyed by language pair, word and translation.
        [HttpPost]
        public async Task<IActionResult> Create([Bind("SourceLanguage,TargetLanguage,Word,Translation,ExampleSentence,ExampleSentenceTranslation,Explanation,Variants,Language")] WordDetail wordDetail)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(wordDetail.Word)
                || string.IsNullOrWhiteSpace(wordDetail.Translation)
                || string.IsNullOrWhiteSpace(wordDetail.SourceLanguage)
                || string.IsNullOrWhiteSpace(wordDetail.TargetLanguage))
            {
                ModelState.AddModelError(string.Empty, "Source language, target language, word and translation are required.");
            }

            if (ModelState.IsValid)
            {
                var key = WordDetailKey.Create(
                    wordDetail.SourceLanguage,
                    wordDetail.TargetLanguage,
                    wordDetail.Word,
                    wordDetail.Translation);
                wordDetail.Id = key.Id;
                wordDetail.SourceLanguage = key.SourceLanguage;
                wordDetail.TargetLanguage = key.TargetLanguage;
                wordDetail.Word = key.Word;
                wordDetail.Translation = key.Translation;
                wordDetail.NormalizedWord = key.NormalizedWord;
                wordDetail.NormalizedTranslation = key.NormalizedTranslation;
                wordDetail.NormalizedWordHash = key.NormalizedWordHash;
                wordDetail.NormalizedTranslationHash = key.NormalizedTranslationHash;
                wordDetail.Language = string.IsNullOrWhiteSpace(wordDetail.Language)
                    ? key.TargetLanguage
                    : wordDetail.Language.Trim();
                wordDetail.Properties = "{}";
                wordDetail.Variants = string.IsNullOrWhiteSpace(wordDetail.Variants)
                    ? "[]"
                    : wordDetail.Variants;
                wordDetail.CreatedAt = DateTimeOffset.UtcNow;
                wordDetail.UpdatedAt = wordDetail.CreatedAt;

                if (await _context.WordDetails.AnyAsync(detail => detail.Id == wordDetail.Id))
                {
                    return RedirectToAction(nameof(Index));
                }

                _context.Add(wordDetail);
                await _context.SaveChangesAsync();
                return RedirectToAction(nameof(Index));
            }
            return View(wordDetail);
        }

        // GET: WordDetails/Edit/5
        public async Task<IActionResult> Edit(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var owned = await LoadOwnedWordDetailAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }
            return View(owned.Value.Detail);
        }

        // POST: WordDetails/Edit/5
        // The existing entity is loaded fresh and only allowlisted fields are copied —
        // never trust the posted QuizId.
        [HttpPost]
        public async Task<IActionResult> Edit(string id, [Bind("Id,ExampleSentence,ExampleSentenceTranslation,Explanation,Variants,Language")] WordDetail posted)
        {
            if (id != posted.Id)
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var owned = await LoadOwnedWordDetailAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }
            var existing = owned.Value.Detail;

            if (!ModelState.IsValid)
            {
                return View(posted);
            }

            existing.ExampleSentence = posted.ExampleSentence;
            existing.ExampleSentenceTranslation = posted.ExampleSentenceTranslation;
            existing.Explanation = posted.Explanation;
            existing.Variants = posted.Variants;
            existing.Language = posted.Language;
            existing.UpdatedAt = DateTimeOffset.UtcNow;

            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException)
            {
                if (!WordDetailExists(existing.Id))
                {
                    return NotFound();
                }
                throw;
            }
            return RedirectToAction(nameof(Index));
        }

        // GET: WordDetails/Delete/5
        public async Task<IActionResult> Delete(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var owned = await LoadOwnedWordDetailAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }

            return View(owned.Value.Detail);
        }

        // POST: WordDetails/Delete/5
        [HttpPost, ActionName("Delete")]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var owned = await LoadOwnedWordDetailAsync(id, userId);
            if (owned == null)
            {
                return NotFound();
            }

            var hasReferences = await _context.Words.AnyAsync(word => word.WordDetailId == id);
            if (hasReferences)
            {
                return Conflict("Shared word details cannot be deleted while words reference them.");
            }

            _context.WordDetails.Remove(owned.Value.Detail);
            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool WordDetailExists(string id)
        {
            return _context.WordDetails.Any(e => e.Id == id);
        }

        private async Task<(WordDetail Detail, Quiz Quiz)?> LoadOwnedWordDetailAsync(string id, string userId)
        {
            var pair = await (
                from word in _context.Words
                join quiz in _context.Quizzes on word.QuizId equals quiz.Id
                join detail in _context.WordDetails on word.WordDetailId equals detail.Id
                where detail.Id == id && quiz.UserId == userId
                select new { detail, quiz }).FirstOrDefaultAsync();

            return pair == null ? null : (pair.detail, pair.quiz);
        }

        private async Task<(WordDetail Detail, Word Word, Quiz Quiz)?> LoadOwnedWordDetailWithWordAsync(string id, string userId)
        {
            var pair = await (
                from word in _context.Words
                join quiz in _context.Quizzes on word.QuizId equals quiz.Id
                join detail in _context.WordDetails on word.WordDetailId equals detail.Id
                where detail.Id == id && quiz.UserId == userId
                select new { detail, word, quiz }).FirstOrDefaultAsync();

            return pair == null ? null : (pair.detail, pair.word, pair.quiz);
        }
    }
}
