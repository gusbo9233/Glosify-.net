using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;

namespace Glosify.Controllers
{
    [Authorize]
    public class WordDetailsController : Controller
    {
        private readonly GlosifyContext _context;

        private static readonly Dictionary<string, ILanguageDictionaryConfig> LanguageConfigs = new()
        {
            ["uk"] = new UkrainianDictionaryConfig(),
            ["de"] = new GermanDictionaryConfig(),
            ["pl"] = new PolishDictionaryConfig(),
            ["et"] = new EstonianDictionaryConfig(),
        };

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
            var hasStoredDictionaryData = wordDetail.Properties != "{}" || wordDetail.Variants != "[]";
            var dictionaryMatch = hasStoredDictionaryData
                ? null
                : await FindDictionaryMatchAsync(wordDetail, word, quiz, detailProperties);
            var dictionaryProperties = WordDetailViewModel.ReadProperties(dictionaryMatch?.Properties);
            var detailVariants = WordDetailViewModel.ReadVariants(wordDetail.Variants);
            var dictionaryVariants = WordDetailViewModel.ReadVariants(dictionaryMatch?.Variants);
            var rawVariants = detailVariants.Any() ? detailVariants : dictionaryVariants;

            var detailLanguage = string.IsNullOrWhiteSpace(wordDetail.Language) ? quiz.TargetLanguage : wordDetail.Language;
            var langCode = LanguageResolver.ResolveLangCode(detailLanguage);
            var pos = LanguageResolver.NormalizePartOfSpeech(WordDetailViewModel.ReadProperties(wordDetail.Properties)
                .FirstOrDefault(p => string.Equals(p.Key, "pos", StringComparison.OrdinalIgnoreCase)).Value);

            WordClassConfig? wordClassConfig = null;
            if (langCode != null && LanguageConfigs.TryGetValue(langCode, out var languageConfig) && !string.IsNullOrEmpty(pos))
            {
                wordClassConfig = languageConfig.GetWordClass(pos);
            }

            var filteredVariants = wordClassConfig != null
                ? WordDetailViewModel.FilterByTags(rawVariants, wordClassConfig.VariantTagFilters)
                : rawVariants;
            if (pos == "Pronoun")
            {
                filteredVariants = WordDetailViewModel.FilterPronounParadigm(
                    filteredVariants, word?.Lemma ?? dictionaryMatch?.Word);
            }

            return View(new WordDetailViewModel
            {
                Detail = wordDetail,
                Word = word,
                Quiz = quiz,
                DictionaryMatch = dictionaryMatch,
                Properties = MergeProperties(dictionaryProperties, detailProperties),
                Variants = filteredVariants,
                WordClassConfig = wordClassConfig
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

        private static readonly Dictionary<string, string[]> SupportedDictionaryLanguages = new(StringComparer.OrdinalIgnoreCase)
        {
            ["de"] = new[] { "de", "german", "deutsch" },
            ["et"] = new[] { "et", "estonian", "eesti" },
            ["uk"] = new[] { "uk", "ukrainian", "ukrainisch", "українська", "українська мова" },
            ["pl"] = new[] { "pl", "polish", "polski", "polnisch" }
        };

        private async Task<DictionaryEntry?> FindDictionaryMatchAsync(
            WordDetail wordDetail,
            Word? word,
            Quiz quiz,
            IReadOnlyList<KeyValuePair<string, string>> detailProperties)
        {
            if (word == null)
            {
                return null;
            }

            var langCode = ResolveSupportedLangCode(wordDetail, quiz);
            if (langCode == null)
            {
                return null;
            }

            var candidates = GetDictionaryWordCandidates(langCode, GetLookupTerms(langCode, word, wordDetail, quiz));
            if (candidates.Count == 0)
            {
                return null;
            }

            var matches = await _context.DictionaryEntries
                .AsNoTracking()
                .Where(entry => entry.LangCode == langCode && candidates.Contains(entry.Word))
                .ToListAsync();

            var selectedPartOfSpeech = GetPropertyValue(detailProperties, "pos");
            var headwordMatch = matches
                .OrderBy(entry => CandidateRank(candidates, entry.Word))
                .ThenBy(entry => PartOfSpeechRank(entry, selectedPartOfSpeech))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Description))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.ExampleSentence))
                .ThenBy(entry => entry.Word.Length)
                .FirstOrDefault();

            var variantMatch = await FindDictionaryVariantMatchAsync(langCode, candidates, selectedPartOfSpeech);
            return PreferVariantParent(headwordMatch, variantMatch);
        }

        private async Task<DictionaryEntry?> FindDictionaryVariantMatchAsync(
            string langCode,
            IReadOnlyList<string> candidates,
            string selectedPartOfSpeech)
        {
            if (candidates.Count == 0)
            {
                return null;
            }

            var allMatches = await _context.DictionaryEntries
                .FromSqlInterpolated(CreateVariantQuery(langCode, candidates))
                .AsNoTracking()
                .ToListAsync();

            foreach (var candidate in candidates)
            {
                var match = allMatches
                    .Where(entry => WordDetailViewModel.ReadVariants(entry.Variants)
                        .Any(v => VariantFormEquals(v.Form, candidate)))
                    .OrderBy(entry => VariantMatchRank(entry, candidate))
                    .ThenBy(entry => PartOfSpeechRank(entry, selectedPartOfSpeech))
                    .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Description))
                    .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.ExampleSentence))
                    .ThenBy(entry => entry.Word.Length)
                    .FirstOrDefault();

                if (match != null)
                {
                    return match;
                }
            }

            return null;
        }

        private static FormattableString CreateVariantQuery(string langCode, IReadOnlyList<string> candidates)
        {
            return candidates.Count switch
            {
                1 => $"""
                        SELECT TOP (20) de.*
                        FROM dbo.dictionary_entries de
                        CROSS APPLY OPENJSON(de.variants) variant
                        WHERE de.lang_code = {langCode}
                        AND JSON_VALUE(variant.value, '$.form') COLLATE Latin1_General_100_CI_AI IN ({candidates[0]})
                    ORDER BY LEN(de.word)
                    """,
                2 => $"""
                    SELECT TOP (20) de.*
                    FROM dbo.dictionary_entries de
                    CROSS APPLY OPENJSON(de.variants) variant
                    WHERE de.lang_code = {langCode}
                        AND JSON_VALUE(variant.value, '$.form') COLLATE Latin1_General_100_CI_AI IN ({candidates[0]}, {candidates[1]})
                    ORDER BY LEN(de.word)
                    """,
                3 => $"""
                    SELECT TOP (20) de.*
                    FROM dbo.dictionary_entries de
                    CROSS APPLY OPENJSON(de.variants) variant
                    WHERE de.lang_code = {langCode}
                        AND JSON_VALUE(variant.value, '$.form') COLLATE Latin1_General_100_CI_AI IN ({candidates[0]}, {candidates[1]}, {candidates[2]})
                    ORDER BY LEN(de.word)
                    """,
                _ => $"""
                    SELECT TOP (20) de.*
                    FROM dbo.dictionary_entries de
                    CROSS APPLY OPENJSON(de.variants) variant
                    WHERE de.lang_code = {langCode}
                        AND JSON_VALUE(variant.value, '$.form') COLLATE Latin1_General_100_CI_AI IN ({candidates[0]}, {candidates[1]}, {candidates[2]}, {candidates[3]})
                    ORDER BY LEN(de.word)
                    """,
            };
        }

        private static int VariantMatchRank(DictionaryEntry entry, string form)
        {
            var variants = WordDetailViewModel.ReadVariants(entry.Variants)
                .Where(variant => VariantFormEquals(variant.Form, form))
                .ToList();

            if (variants.Any(variant => variant.HasAnyTag("singular")))
            {
                return 0;
            }

            if (variants.Any(variant => !variant.HasAnyTag("plural")))
            {
                return 1;
            }

            return 2;
        }

        private static bool VariantFormEquals(string left, string right)
        {
            return string.Equals(left, right, StringComparison.Ordinal)
                || string.Equals(
                    StripCombiningMarks(left),
                    StripCombiningMarks(right),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string StripCombiningMarks(string value)
        {
            var normalized = value.Normalize(NormalizationForm.FormD);
            var builder = new StringBuilder(normalized.Length);
            foreach (var ch in normalized)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
                {
                    builder.Append(ch);
                }
            }

            return builder.ToString().Normalize(NormalizationForm.FormC);
        }

        private static DictionaryEntry? PreferVariantParent(DictionaryEntry? headwordMatch, DictionaryEntry? variantMatch)
        {
            if (headwordMatch == null)
            {
                return variantMatch;
            }

            if (variantMatch == null)
            {
                return headwordMatch;
            }

            return IsInflectionStub(headwordMatch) && !IsInflectionStub(variantMatch)
                ? variantMatch
                : headwordMatch;
        }

        private static bool IsInflectionStub(DictionaryEntry entry)
        {
            var properties = WordDetailViewModel.ReadProperties(entry.Properties);
            var tags = properties
                .Where(property => string.Equals(property.Key, "tags", StringComparison.OrdinalIgnoreCase))
                .Select(property => property.Value)
                .FirstOrDefault() ?? string.Empty;

            return tags.Contains("form of", StringComparison.OrdinalIgnoreCase)
                || (entry.Description?.Contains(" of ", StringComparison.OrdinalIgnoreCase) == true
                    && (entry.Description.Contains("inflection", StringComparison.OrdinalIgnoreCase)
                        || entry.Description.Contains("participle of", StringComparison.OrdinalIgnoreCase)
                        || entry.Description.Contains("connegative of", StringComparison.OrdinalIgnoreCase)));
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

        private static string? ResolveSupportedLangCode(WordDetail wordDetail, Quiz quiz)
        {
            string?[] languages = { wordDetail.Language, quiz.TargetLanguage, quiz.Language, quiz.SourceLanguage };
            foreach (var language in languages)
            {
                var code = MatchSupportedLangCode(language);
                if (code != null)
                {
                    return code;
                }
            }

            return null;
        }

        private static string? MatchSupportedLangCode(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            foreach (var (code, markers) in SupportedDictionaryLanguages)
            {
                foreach (var marker in markers)
                {
                    if (language.Equals(marker, StringComparison.OrdinalIgnoreCase)
                        || language.Contains(marker, StringComparison.OrdinalIgnoreCase))
                    {
                        return code;
                    }
                }
            }

            return null;
        }

        private static IEnumerable<string> GetLookupTerms(string langCode, Word word, WordDetail wordDetail, Quiz quiz)
        {
            if (MatchSupportedLangCode(quiz.TargetLanguage) == langCode
                || MatchSupportedLangCode(wordDetail.Language) == langCode
                || MatchSupportedLangCode(quiz.Language) == langCode)
            {
                yield return word.Lemma;
            }

            if (MatchSupportedLangCode(quiz.SourceLanguage) == langCode)
            {
                yield return word.Translation;
            }

            yield return word.Lemma;
            yield return word.Translation;
        }

        private static List<string> GetDictionaryWordCandidates(string langCode, IEnumerable<string> lookupTerms)
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

                if (langCode == "de")
                {
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
