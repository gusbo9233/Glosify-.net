using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
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

        public WordDetailsController(
            GlosifyContext context,
            IWordDetailViewModelService viewModelService)
        {
            _context = context;
            _viewModelService = viewModelService;
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
                        _context.Quizzes.Where(q => q.UserId.ToString() == userId),
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

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> UpdatePartOfSpeech(string id, string? partOfSpeech)
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
            var wordDetail = owned.Value.Detail;

            var properties = ReadPropertiesObject(wordDetail.Properties);
            if (string.IsNullOrWhiteSpace(partOfSpeech))
            {
                properties.Remove("pos");
            }
            else
            {
                properties["pos"] = partOfSpeech.Trim();
            }

            wordDetail.Properties = properties.ToJsonString(new JsonSerializerOptions { WriteIndented = false });
            await _context.SaveChangesAsync();

            return RedirectToAction(nameof(Details), new { id });
        }

        // POST: WordDetails/Create
        // Word details are shared cache rows keyed by language pair, word and translation.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("SourceLanguage,TargetLanguage,Word,Translation,Properties,ExampleSentence,Explanation,Variants,Language")] WordDetail wordDetail)
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
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("Id,Properties,ExampleSentence,Explanation,Variants,Language")] WordDetail posted)
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

            existing.Properties = posted.Properties;
            existing.ExampleSentence = posted.ExampleSentence;
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
        [ValidateAntiForgeryToken]
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
                where detail.Id == id && quiz.UserId.ToString() == userId
                select new { detail, quiz }).FirstOrDefaultAsync();

            return pair == null ? null : (pair.detail, pair.quiz);
        }

        private static JsonObject ReadPropertiesObject(string? json)
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                return [];
            }

            try
            {
                var node = JsonNode.Parse(json);
                return node as JsonObject ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
