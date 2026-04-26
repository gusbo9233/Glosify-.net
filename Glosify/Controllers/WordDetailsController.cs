using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Glosify.Data;
using Glosify.Models;

namespace Glosify.Controllers
{
    [Authorize]
    public class WordDetailsController : Controller
    {
        private readonly GlosifyContext _context;

        public WordDetailsController(GlosifyContext context)
        {
            _context = context;
        }

        // GET: WordDetails
        public async Task<IActionResult> Index()
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var wordDetails = await _context.WordDetails
                .Join(
                    _context.Quizzes.Where(q => q.UserId.ToString() == userId),
                    wordDetail => wordDetail.QuizId,
                    quiz => quiz.Id,
                    (wordDetail, _) => wordDetail)
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

            var wordDetail = await _context.WordDetails
                .FirstOrDefaultAsync(m => m.Id == id);
            if (wordDetail == null)
            {
                return NotFound();
            }

            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == wordDetail.QuizId && q.UserId.ToString() == userId);
            if (quiz == null)
            {
                return NotFound();
            }

            var word = await _context.Words
                .FirstOrDefaultAsync(w => w.WordDetailId == wordDetail.Id && w.QuizId == wordDetail.QuizId);

            var detailProperties = WordDetailViewModel.ReadProperties(wordDetail.Properties);
            var dictionaryMatch = await FindDictionaryMatchAsync(wordDetail, word, quiz, detailProperties);
            var dictionaryProperties = WordDetailViewModel.ReadProperties(dictionaryMatch?.Properties);
            var detailVariants = WordDetailViewModel.ReadVariants(wordDetail.Variants);
            var dictionaryVariants = WordDetailViewModel.ReadVariants(dictionaryMatch?.Variants);

            return View(new WordDetailViewModel
            {
                Detail = wordDetail,
                Word = word,
                Quiz = quiz,
                DictionaryMatch = dictionaryMatch,
                Properties = MergeProperties(dictionaryProperties, detailProperties),
                Variants = detailVariants.Any() ? detailVariants : dictionaryVariants
            });
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

            var wordDetail = await _context.WordDetails.FirstOrDefaultAsync(m => m.Id == id);
            if (wordDetail == null)
            {
                return NotFound();
            }

            var ownsQuiz = await _context.Quizzes
                .AnyAsync(q => q.Id == wordDetail.QuizId && q.UserId.ToString() == userId);
            if (!ownsQuiz)
            {
                return NotFound();
            }

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
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Create([Bind("QuizId,Id,Properties,ExampleSentence,Explanation,Variants,Language")] WordDetail wordDetail)
        {
            if (ModelState.IsValid)
            {
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

            var wordDetail = await _context.WordDetails.FindAsync(id);
            if (wordDetail == null)
            {
                return NotFound();
            }
            return View(wordDetail);
        }

        // POST: WordDetails/Edit/5
        // To protect from overposting attacks, enable the specific properties you want to bind to.
        // For more details, see http://go.microsoft.com/fwlink/?LinkId=317598.
        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, [Bind("QuizId,Id,Properties,ExampleSentence,Explanation,Variants,Language")] WordDetail wordDetail)
        {
            if (id != wordDetail.Id)
            {
                return NotFound();
            }

            if (ModelState.IsValid)
            {
                try
                {
                    _context.Update(wordDetail);
                    await _context.SaveChangesAsync();
                }
                catch (DbUpdateConcurrencyException)
                {
                    if (!WordDetailExists(wordDetail.Id))
                    {
                        return NotFound();
                    }
                    else
                    {
                        throw;
                    }
                }
                return RedirectToAction(nameof(Index));
            }
            return View(wordDetail);
        }

        // GET: WordDetails/Delete/5
        public async Task<IActionResult> Delete(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
            {
                return NotFound();
            }

            var wordDetail = await _context.WordDetails
                .FirstOrDefaultAsync(m => m.Id == id);
            if (wordDetail == null)
            {
                return NotFound();
            }

            return View(wordDetail);
        }

        // POST: WordDetails/Delete/5
        [HttpPost, ActionName("Delete")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteConfirmed(string id)
        {
            var wordDetail = await _context.WordDetails.FindAsync(id);
            if (wordDetail != null)
            {
                _context.WordDetails.Remove(wordDetail);
            }

            await _context.SaveChangesAsync();
            return RedirectToAction(nameof(Index));
        }

        private bool WordDetailExists(string id)
        {
            return _context.WordDetails.Any(e => e.Id == id);
        }

        private async Task<DictionaryEntry?> FindDictionaryMatchAsync(
            WordDetail wordDetail,
            Word? word,
            Quiz quiz,
            IReadOnlyList<KeyValuePair<string, string>> detailProperties)
        {
            if (word == null || !IsGermanLanguage(wordDetail.Language, quiz.TargetLanguage, quiz.Language))
            {
                return null;
            }

            var candidates = GetDictionaryWordCandidates(GetGermanLookupTerms(word, wordDetail, quiz));
            if (candidates.Count == 0)
            {
                return null;
            }

            var matches = await _context.DictionaryEntries
                .AsNoTracking()
                .Where(entry => entry.LangCode == "de" && candidates.Contains(entry.Word))
                .ToListAsync();

            if (matches.Count == 0)
            {
                return null;
            }

            var selectedPartOfSpeech = GetPropertyValue(detailProperties, "pos");

            return matches
                .OrderBy(entry => CandidateRank(candidates, entry.Word))
                .ThenBy(entry => PartOfSpeechRank(entry, selectedPartOfSpeech))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Description))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.ExampleSentence))
                .ThenBy(entry => entry.Word.Length)
                .FirstOrDefault();
        }

        private static IReadOnlyList<KeyValuePair<string, string>> MergeProperties(
            IReadOnlyList<KeyValuePair<string, string>> dictionaryProperties,
            IReadOnlyList<KeyValuePair<string, string>> detailProperties)
        {
            if (dictionaryProperties.Count == 0)
            {
                return detailProperties;
            }

            if (detailProperties.Count == 0)
            {
                return dictionaryProperties;
            }

            var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in dictionaryProperties.Concat(detailProperties))
            {
                merged[property.Key] = property.Value;
            }

            return merged.Select(property => new KeyValuePair<string, string>(property.Key, property.Value)).ToList();
        }

        private static string GetPropertyValue(IReadOnlyList<KeyValuePair<string, string>> properties, string key)
        {
            return properties
                .FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;
        }

        private static int CandidateRank(IReadOnlyList<string> candidates, string word)
        {
            for (var index = 0; index < candidates.Count; index++)
            {
                if (string.Equals(candidates[index], word, StringComparison.Ordinal))
                {
                    return index * 2;
                }
            }

            for (var index = 0; index < candidates.Count; index++)
            {
                if (string.Equals(candidates[index], word, StringComparison.OrdinalIgnoreCase))
                {
                    return (index * 2) + 1;
                }
            }

            return candidates.Count * 2;
        }

        private static int PartOfSpeechRank(DictionaryEntry entry, string selectedPartOfSpeech)
        {
            if (string.IsNullOrWhiteSpace(selectedPartOfSpeech))
            {
                return string.IsNullOrWhiteSpace(entry.PartOfSpeech) ? 1 : 0;
            }

            return string.Equals(entry.PartOfSpeech, selectedPartOfSpeech, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
        }

        private static IEnumerable<string> GetGermanLookupTerms(Word word, WordDetail wordDetail, Quiz quiz)
        {
            if (IsGermanLanguage(quiz.TargetLanguage, wordDetail.Language, quiz.Language))
            {
                yield return word.Translation;
            }

            if (IsGermanLanguage(quiz.SourceLanguage))
            {
                yield return word.Lemma;
            }

            yield return word.Translation;
            yield return word.Lemma;
        }

        private static List<string> GetDictionaryWordCandidates(IEnumerable<string> lookupTerms)
        {
            var candidates = new List<string>();
            foreach (var lookupTerm in lookupTerms)
            {
                var trimmed = lookupTerm.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                {
                    continue;
                }

                AddCandidateWithCaseVariants(candidates, trimmed);

                var articlePrefixes = new[] { "der ", "die ", "das ", "ein ", "eine ", "einen ", "einem ", "einer " };
                foreach (var prefix in articlePrefixes)
                {
                    if (trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        AddCandidateWithCaseVariants(candidates, trimmed[prefix.Length..].Trim());
                        break;
                    }
                }
            }

            return candidates;
        }

        private static void AddCandidateWithCaseVariants(List<string> candidates, string value)
        {
            AddCandidate(candidates, value);

            var trimmed = value.Trim();
            if (trimmed.Length > 0)
            {
                AddCandidate(candidates, string.Concat(char.ToUpperInvariant(trimmed[0]).ToString(), trimmed[1..]));
            }
        }

        private static void AddCandidate(List<string> candidates, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !candidates.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal)))
            {
                candidates.Add(value);
            }
        }

        private static bool IsGermanLanguage(params string[] languages)
        {
            return languages.Any(language =>
                !string.IsNullOrWhiteSpace(language)
                && (language.Equals("de", StringComparison.OrdinalIgnoreCase)
                    || language.Contains("german", StringComparison.OrdinalIgnoreCase)
                    || language.Contains("deutsch", StringComparison.OrdinalIgnoreCase)));
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
