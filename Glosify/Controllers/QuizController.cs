using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.EntityFrameworkCore;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services;
using Google.GenAI;
using Google.GenAI.Types;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

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
            var wordDetails = await _context.WordDetails
                .Where(detail => detail.QuizId == selectedQuiz.Id)
                .ToListAsync();
            var sentences = wordDetails
                .Where(detail => !string.IsNullOrWhiteSpace(detail.ExampleSentence))
                .GroupBy(detail => detail.ExampleSentence.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(group => new QuizSentenceViewModel
                {
                    Text = group.Key,
                    Translation = group
                        .Select(detail => detail.Explanation.Trim())
                        .FirstOrDefault(translation => !string.IsNullOrWhiteSpace(translation)) ?? string.Empty,
                    WordCount = group.Count()
                })
                .OrderBy(sentence => sentence.Text)
                .ToList();

            return View("quiz-view", new QuizWorkspaceViewModel
            {
                SelectedQuiz = selectedQuiz,
                Words = words,
                Sentences = sentences
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
                var dictionaryMatch = await FindManualDictionaryMatchAsync(quiz.TargetLanguage, word);
                _context.WordDetails.Add(new WordDetail
                {
                    Id = wordDetailId,
                    QuizId = quizId,
                    Language = quiz.TargetLanguage,
                    Properties = dictionaryMatch?.Properties ?? "{}",
                    Variants = dictionaryMatch?.Variants ?? "[]",
                    Explanation = dictionaryMatch?.Description ?? string.Empty,
                    ExampleSentence = dictionaryMatch?.ExampleSentence ?? string.Empty
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
        public async Task<IActionResult> GenerateWords(Guid quizId, string input)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == quizId && q.UserId.ToString() == userId);

            if (quiz == null)
                return RedirectToAction("Index");

            if (string.IsNullOrWhiteSpace(input))
            {
                TempData["AiError"] = "Paste some text first so the assistant has vocabulary to extract.";
                return RedirectToAction("Details", new { id = quizId });
            }

            var sourceText = input.Trim();
            var json = await GenerateWordsWithAssistant(sourceText, quiz.SourceLanguage, quiz.TargetLanguage);
            if (!ValidateGeneratedWordsResponse(json))
            {
                TempData["AiError"] = "The assistant returned an unexpected response. Try a shorter text sample.";
                return RedirectToAction("Details", new { id = quizId });
            }

            var generatedWords = JsonSerializer.Deserialize<Dictionary<string, GeneratedWord>>(json) ?? [];
            var sourceSentences = ExtractSourceSentences(sourceText);
            var shouldUseSourceSentences = ShouldUseSourceSentences(sourceSentences);
            var existingWords = await _context.Words
                .Where(w => w.QuizId == quizId)
                .Select(w => w.Lemma)
                .ToListAsync();
            var existing = new HashSet<string>(existingWords, StringComparer.OrdinalIgnoreCase);
            var added = 0;

            foreach (var (lemma, generatedWord) in generatedWords)
            {
                var trimmedLemma = lemma.Trim();
                var translation = generatedWord.Translation?.Trim();

                if (string.IsNullOrWhiteSpace(trimmedLemma)
                    || string.IsNullOrWhiteSpace(translation)
                    || existing.Contains(trimmedLemma))
                {
                    continue;
                }

                var wordDetailId = Guid.NewGuid().ToString("N");
                _context.WordDetails.Add(new WordDetail
                {
                    Id = wordDetailId,
                    QuizId = quizId,
                    Language = quiz.TargetLanguage,
                    ExampleSentence = ResolveExampleSentence(trimmedLemma, generatedWord, sourceSentences, shouldUseSourceSentences),
                    Explanation = generatedWord.ExampleSentenceTranslation?.Trim() ?? string.Empty
                });

                _context.Words.Add(new Word
                {
                    Id = Guid.NewGuid().ToString("N"),
                    QuizId = quizId,
                    Lemma = trimmedLemma,
                    Translation = translation,
                    WordDetailId = wordDetailId
                });

                existing.Add(trimmedLemma);
                added++;
            }

            if (added > 0)
            {
                await _context.SaveChangesAsync();
                TempData["AiMessage"] = $"Added {added} generated {(added == 1 ? "word" : "words")}.";
            }
            else
            {
                TempData["AiMessage"] = "No new words were added. The generated words may already be in this quiz.";
            }

            return RedirectToAction("Details", new { id = quizId });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteWord(string id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            if (string.IsNullOrWhiteSpace(id))
                return RedirectToAction("Index");

            var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == id);
            if (word == null)
                return RedirectToAction("Index");

            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == word.QuizId && q.UserId.ToString() == userId);

            if (quiz == null)
                return RedirectToAction("Index");

            var wordDetail = await _context.WordDetails
                .FirstOrDefaultAsync(detail => detail.Id == word.WordDetailId && detail.QuizId == quiz.Id);

            _context.Words.Remove(word);
            await _context.SaveChangesAsync();

            if (wordDetail != null)
            {
                _context.WordDetails.Remove(wordDetail);
                await _context.SaveChangesAsync();
            }

            TempData["QuizMessage"] = $"Deleted {word.Lemma}.";

            return RedirectToAction("Details", new { id = quiz.Id });
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteQuiz(Guid id)
        {
            var userId = User.FindFirstValue(ClaimTypes.NameIdentifier);
            if (string.IsNullOrEmpty(userId))
                return RedirectToAction("Login", "Account");

            var quiz = await _context.Quizzes
                .FirstOrDefaultAsync(q => q.Id == id && q.UserId.ToString() == userId);

            if (quiz == null)
                return RedirectToAction("Index");

            var words = await _context.Words
                .Where(word => word.QuizId == quiz.Id)
                .ToListAsync();
            var wordDetails = await _context.WordDetails
                .Where(detail => detail.QuizId == quiz.Id)
                .ToListAsync();

            _context.Words.RemoveRange(words);
            await _context.SaveChangesAsync();

            _context.WordDetails.RemoveRange(wordDetails);
            await _context.SaveChangesAsync();

            _context.Quizzes.Remove(quiz);
            await _context.SaveChangesAsync();

            TempData["QuizMessage"] = $"Deleted {quiz.Name}.";

            return RedirectToAction("Index");
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

        private async Task<DictionaryEntry?> FindManualDictionaryMatchAsync(string language, string word)
        {
            var langCode = MatchSupportedLangCode(language);
            if (langCode == null || string.IsNullOrWhiteSpace(word))
            {
                return null;
            }

            var candidates = GetManualDictionaryCandidates(word);
            var matches = await _context.DictionaryEntries
                .AsNoTracking()
                .Where(entry => entry.LangCode == langCode && candidates.Contains(entry.Word))
                .ToListAsync();

            var headwordMatch = matches
                .OrderBy(entry => CandidateRank(candidates, entry.Word))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Description))
                .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.ExampleSentence))
                .ThenBy(entry => entry.Word.Length)
                .FirstOrDefault();

            var variantMatch = await FindManualDictionaryVariantMatchAsync(langCode, candidates);
            return PreferVariantParent(headwordMatch, variantMatch);
        }

        private async Task<DictionaryEntry?> FindManualDictionaryVariantMatchAsync(string langCode, IReadOnlyList<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                var matches = await _context.DictionaryEntries
                    .FromSqlInterpolated($"""
                        SELECT TOP (20) de.*
                        FROM dbo.dictionary_entries de
                        CROSS APPLY OPENJSON(de.variants) variant
                        WHERE de.lang_code = {langCode}
                            AND JSON_VALUE(variant.value, '$.form') = {candidate}
                        ORDER BY
                            CASE WHEN JSON_QUERY(variant.value, '$.tags') LIKE '%"singular"%' THEN 0 ELSE 1 END,
                            CASE WHEN JSON_QUERY(variant.value, '$.tags') LIKE '%"plural"%' THEN 1 ELSE 0 END,
                            LEN(de.word)
                        """)
                    .AsNoTracking()
                    .ToListAsync();

                var match = matches
                    .OrderBy(entry => VariantMatchRank(entry, candidate))
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

        private static int VariantMatchRank(DictionaryEntry entry, string form)
        {
            var variants = WordDetailViewModel.ReadVariants(entry.Variants)
                .Where(variant => string.Equals(variant.Form, form, StringComparison.Ordinal))
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

        private static string? MatchSupportedLangCode(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return null;
            }

            if (language.Equals("de", StringComparison.OrdinalIgnoreCase)
                || language.Contains("german", StringComparison.OrdinalIgnoreCase)
                || language.Contains("deutsch", StringComparison.OrdinalIgnoreCase))
            {
                return "de";
            }

            if (language.Equals("et", StringComparison.OrdinalIgnoreCase)
                || language.Contains("estonian", StringComparison.OrdinalIgnoreCase)
                || language.Contains("eesti", StringComparison.OrdinalIgnoreCase))
            {
                return "et";
            }

            if (language.Equals("uk", StringComparison.OrdinalIgnoreCase)
                || language.Contains("ukrainian", StringComparison.OrdinalIgnoreCase)
                || language.Contains("ukrainisch", StringComparison.OrdinalIgnoreCase)
                || language.Contains("українськ", StringComparison.OrdinalIgnoreCase))
            {
                return "uk";
            }

            if (language.Equals("pl", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polish", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polski", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polnisch", StringComparison.OrdinalIgnoreCase))
            {
                return "pl";
            }

            return null;
        }

        private static IReadOnlyList<string> GetManualDictionaryCandidates(string word)
        {
            var candidates = new List<string>();
            AddManualDictionaryCandidate(candidates, word.Trim());

            if (!string.IsNullOrWhiteSpace(word))
            {
                var trimmed = word.Trim();
                AddManualDictionaryCandidate(candidates, string.Concat(char.ToUpperInvariant(trimmed[0]).ToString(), trimmed[1..]));
                AddManualDictionaryCandidate(candidates, string.Concat(char.ToLowerInvariant(trimmed[0]).ToString(), trimmed[1..]));
            }

            return candidates;
        }

        private static void AddManualDictionaryCandidate(List<string> candidates, string value)
        {
            if (!string.IsNullOrWhiteSpace(value)
                && !candidates.Any(candidate => string.Equals(candidate, value, StringComparison.Ordinal)))
            {
                candidates.Add(value);
            }
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

        private static async Task<string> GenerateWordsWithAssistant(string input, string knownLanguage, string targetLanguage)
        {
            var apiKey = LoadGeminiApiKey();
            var prompt = BuildWordExtractionPrompt(input, knownLanguage, targetLanguage);
            var client = new Client(apiKey: apiKey);
            var response = await client.Models.GenerateContentAsync(
                model: "gemini-2.5-flash-lite",
                contents: prompt,
                config: new GenerateContentConfig { ResponseMimeType = "application/json" }
            );

            return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
        }

        private static string BuildWordExtractionPrompt(string input, string knownLanguage, string targetLanguage)
        {
            var candidateWords = ExtractCandidateWords(input);
            var candidates = string.Join("\n", candidateWords.Select(word => $"- {word}"));
            var extractedSourceSentences = ExtractSourceSentences(input);
            var useSourceSentences = ShouldUseSourceSentences(extractedSourceSentences);
            var sourceSentences = string.Join("\n", extractedSourceSentences.Select(sentence => $"- {sentence}"));
            var exampleSentenceRule = useSourceSentences
                ? "- \"\"example_sentence\"\": the exact source sentence or phrase from the list below that contains the word"
                : $"- \"\"example_sentence\"\": a natural example sentence in {targetLanguage} using the word";
            var exampleSentenceTranslationRule = useSourceSentences
                ? $"- \"\"example_sentence_translation\"\": the {knownLanguage} translation of that exact source sentence or phrase"
                : $"- \"\"example_sentence_translation\"\": the {knownLanguage} translation of the example sentence";

            return $@"
        The user knows {knownLanguage} and is learning {targetLanguage}.
        Extract vocabulary from the input below and return a JSON object.

        Rules:
        - Output MUST be valid JSON only. No explanations, no extra text.
        - Include EVERY distinct candidate word listed below.
        - Preserve each candidate word exactly as written, including inflected forms.
        - Include proper nouns for places, countries, languages, and nationalities.
        - Include short/common words too, such as auxiliaries, prepositions, and adverbs.
        - Do not merge separate candidate words into phrases.
        - Each key is one candidate word in {targetLanguage}.
        - Each value is an object with:
        - ""translation"": the {knownLanguage} translation of the word
        {exampleSentenceRule}
        {exampleSentenceTranslationRule}

        Format:
        {{
        ""word1"": {{
            ""translation"": ""..."",
            ""example_sentence"": ""..."",
            ""example_sentence_translation"": ""...""
        }}
        }}

        Candidate words:
        {candidates}

        Source sentences:
        {sourceSentences}

        Input:
        {input}";
        }

        private static IReadOnlyList<string> ExtractCandidateWords(string input)
        {
            return Regex.Matches(input, @"[\p{L}\p{M}]+(?:['’][\p{L}\p{M}]+)?")
                .Select(match => match.Value.Trim())
                .Where(word => !string.IsNullOrWhiteSpace(word))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IReadOnlyList<string> ExtractSourceSentences(string input)
        {
            return Regex.Split(input, @"(?<=[.!?])\s+|\r?\n+")
                .Select(sentence => sentence.Trim())
                .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
                .ToList();
        }

        private static bool ShouldUseSourceSentences(IReadOnlyList<string> sourceSentences)
        {
            if (sourceSentences.Count == 0)
            {
                return false;
            }

            return sourceSentences.Any(sentence => ExtractCandidateWords(sentence).Count > 2);
        }

        private static string ResolveExampleSentence(
            string word,
            GeneratedWord generatedWord,
            IReadOnlyList<string> sourceSentences,
            bool shouldUseSourceSentences)
        {
            if (shouldUseSourceSentences)
            {
                var sourceSentence = FindSourceSentence(word, sourceSentences);
                if (!string.IsNullOrWhiteSpace(sourceSentence))
                {
                    return sourceSentence;
                }
            }

            return generatedWord.ExampleSentence?.Trim() ?? string.Empty;
        }

        private static string? FindSourceSentence(string word, IReadOnlyList<string> sourceSentences)
        {
            var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word)}(?![\p{{L}}\p{{M}}])";
            return sourceSentences.FirstOrDefault(sentence => Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase));
        }

        private static bool ValidateGeneratedWordsResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    var val = entry.Value;
                    if (val.ValueKind != JsonValueKind.Object) return false;
                    if (!val.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.String) return false;
                    if (!val.TryGetProperty("example_sentence", out var example) || example.ValueKind != JsonValueKind.String) return false;
                    if (!val.TryGetProperty("example_sentence_translation", out var exampleTranslation) || exampleTranslation.ValueKind != JsonValueKind.String) return false;
                }

                return true;
            }
            catch (JsonException)
            {
                return false;
            }
        }

        private static string LoadGeminiApiKey()
        {
            var envFile = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".env");
            if (System.IO.File.Exists(envFile))
            {
                foreach (var line in System.IO.File.ReadAllLines(envFile))
                {
                    var parts = line.Split('=', 2);
                    if (parts.Length == 2)
                    {
                        System.Environment.SetEnvironmentVariable(parts[0].Trim(), parts[1].Trim());
                    }
                }
            }

            return System.Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? string.Empty;
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

    public class GeneratedWord
    {
        [JsonPropertyName("translation")]
        public string? Translation { get; set; }

        [JsonPropertyName("example_sentence")]
        public string? ExampleSentence { get; set; }

        [JsonPropertyName("example_sentence_translation")]
        public string? ExampleSentenceTranslation { get; set; }
    }
}
