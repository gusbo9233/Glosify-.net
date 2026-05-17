using System.Text.RegularExpressions;
using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class GeneratedVocabularyService : IGeneratedVocabularyService
{
    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly IQuizServerVocabularyGenerationService _quizServerVocabularyGenerationService;
    private readonly ILogger<GeneratedVocabularyService> _logger;

    public GeneratedVocabularyService(
        GlosifyContext context,
        IQuizService quizService,
        IQuizServerVocabularyGenerationService quizServerVocabularyGenerationService,
        ILogger<GeneratedVocabularyService> logger)
    {
        _context = context;
        _quizService = quizService;
        _quizServerVocabularyGenerationService = quizServerVocabularyGenerationService;
        _logger = logger;
    }

    public async Task<GeneratedVocabularyResult> GenerateAndAddWordsAsync(Guid quizId, string userId, string input)
    {
        Quiz? quiz;
        try
        {
            quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Database was not ready while loading quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
        }

        if (quiz == null)
        {
            return GeneratedVocabularyResult.Failure("Quiz not found.");
        }

        if (string.IsNullOrWhiteSpace(input))
        {
            return GeneratedVocabularyResult.Failure("Paste some text first so the assistant has vocabulary to extract.");
        }

        IReadOnlyDictionary<string, GeneratedWord> generatedWords;
        IReadOnlyList<string> sourceSentences;
        try
        {
            var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
            sourceSentences = ExtractSourceSentences(cleanedInput);
            generatedWords = await _quizServerVocabularyGenerationService.GenerateWordsFromTextAsync(
                cleanedInput,
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                quiz.Name);
            _logger.LogInformation(
                "Generated vocabulary for quiz {QuizId}: quiz server, cleaned length {CleanedLength}, source sentence count {SourceSentenceCount}, generated item count {GeneratedCount}",
                quizId,
                cleanedInput.Length,
                sourceSentences.Count,
                generatedWords.Count);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI vocabulary generation returned an unexpected response for quiz {QuizId}", quizId);
            if (ex.Message.StartsWith("The quiz server", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("could not find", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No useful vocabulary", StringComparison.OrdinalIgnoreCase))
            {
                return GeneratedVocabularyResult.Failure(ex.Message);
            }

            return GeneratedVocabularyResult.Failure("The assistant returned an unexpected response. Try a shorter text sample.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Quiz server request failed for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.QuizServer);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Quiz server request timed out for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.QuizServer);
        }

        List<ExistingWordRow> existingWordRows;
        Dictionary<string, WordDetail> existingWordDetails;
        try
        {
            existingWordRows = await _context.Words
                .Where(w => w.QuizId == quizId)
                .Select(w => new ExistingWordRow(w.Lemma, w.WordDetailId))
                .ToListAsync();
            var existingWordDetailIds = existingWordRows.Select(word => word.WordDetailId);
            existingWordDetails = await _context.WordDetails
                .Where(detail => existingWordDetailIds.Contains(detail.Id))
                .ToDictionaryAsync(detail => detail.Id);
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Database was not ready while loading existing words for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
        }

        var existing = new HashSet<string>(existingWordRows.Select(word => word.Lemma), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var repairedExisting = 0;
        var skippedBlank = 0;
        var skippedPronunciation = 0;
        var skippedExisting = 0;
        var skippedExample = 0;

        foreach (var (lemma, generatedWord) in generatedWords)
        {
            var trimmedLemma = lemma.Trim();
            var translation = generatedWord.Translation?.Trim();

            if (string.IsNullOrWhiteSpace(trimmedLemma) || string.IsNullOrWhiteSpace(translation))
            {
                skippedBlank++;
                continue;
            }

            if (IsPronunciationHint(translation))
            {
                skippedPronunciation++;
                continue;
            }

            if (existing.Contains(trimmedLemma))
            {
                if (TryRepairExistingExampleSentence(
                    trimmedLemma,
                    translation,
                    generatedWord,
                    sourceSentences,
                    existingWordRows
                        .Where(word => string.Equals(word.Lemma, trimmedLemma, StringComparison.OrdinalIgnoreCase))
                        .Select(word => word.WordDetailId),
                    existingWordDetails))
                {
                    repairedExisting++;
                }

                skippedExisting++;
                continue;
            }

            var aiExampleSentence = ResolveExampleSentence(trimmedLemma, translation, generatedWord, sourceSentences);
            var aiExampleSentenceTranslation = generatedWord.ExampleSentenceTranslation?.Trim() ?? string.Empty;
            if (!IsUsefulExampleSentence(trimmedLemma, translation, aiExampleSentence, aiExampleSentenceTranslation))
            {
                aiExampleSentence = string.Empty;
                aiExampleSentenceTranslation = string.Empty;
                skippedExample++;
            }

            WordDetail wordDetail;
            try
            {
                wordDetail = await GetOrCreateWordDetailAsync(
                    quiz.SourceLanguage,
                    quiz.TargetLanguage,
                    trimmedLemma,
                    translation);
            }
            catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
            {
                _logger.LogWarning(ex, "Database was not ready while preparing generated word detail for quiz {QuizId}", quizId);
                return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
            }
            var shouldReplaceExampleSentence = ShouldReplaceExampleSentence(
                trimmedLemma,
                translation,
                wordDetail.ExampleSentence,
                wordDetail.ExampleSentenceTranslation);
            if (shouldReplaceExampleSentence && !string.IsNullOrWhiteSpace(aiExampleSentence))
            {
                wordDetail.ExampleSentence = aiExampleSentence;
                wordDetail.UpdatedAt = DateTimeOffset.UtcNow;
            }

            if ((shouldReplaceExampleSentence || string.IsNullOrWhiteSpace(wordDetail.ExampleSentenceTranslation))
                && !string.IsNullOrWhiteSpace(aiExampleSentenceTranslation))
            {
                wordDetail.ExampleSentenceTranslation = aiExampleSentenceTranslation;
                wordDetail.UpdatedAt = DateTimeOffset.UtcNow;
            }

            _context.Words.Add(new Word
            {
                Id = Guid.NewGuid().ToString("N"),
                QuizId = quizId,
                Lemma = trimmedLemma,
                Translation = translation,
                WordDetailId = wordDetail.Id
            });

            existing.Add(trimmedLemma);
            added++;
        }

        _logger.LogInformation(
            "Generated vocabulary import for quiz {QuizId}: added {AddedCount}, repaired existing {RepairedExistingCount}, skipped blank {SkippedBlankCount}, pronunciation {SkippedPronunciationCount}, existing {SkippedExistingCount}, example cleanup {SkippedExampleCount}",
            quizId,
            added,
            repairedExisting,
            skippedBlank,
            skippedPronunciation,
            skippedExisting,
            skippedExample);

        if (added == 0 && repairedExisting == 0)
        {
            return GeneratedVocabularyResult.Success(0, "No new words were added. The generated words may already be in this quiz.");
        }

        try
        {
            await _context.SaveChangesAsync();
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Database was not ready while saving generated words for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
        }
        if (added == 0)
        {
            return GeneratedVocabularyResult.Success(0, $"Updated {repairedExisting} existing example {(repairedExisting == 1 ? "sentence" : "sentences")}.");
        }

        return GeneratedVocabularyResult.Success(added, $"Added {added} generated {(added == 1 ? "word" : "words")}.");
    }

    private sealed record ExistingWordRow(string Lemma, string WordDetailId);

    private static IReadOnlyList<string> ExtractSourceSentences(string input)
    {
        return Regex.Split(VocabularyInputCleaner.CleanForVocabulary(input), @"(?<=[.!?])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static string ResolveExampleSentence(
        string word,
        string translation,
        GeneratedWord generatedWord,
        IReadOnlyList<string> sourceSentences)
    {
        var sourceSentence = FindSourceSentence(word, translation, sourceSentences);
        return !string.IsNullOrWhiteSpace(sourceSentence) ? sourceSentence : generatedWord.ExampleSentence?.Trim() ?? string.Empty;
    }

    private static bool TryRepairExistingExampleSentence(
        string word,
        string translation,
        GeneratedWord generatedWord,
        IReadOnlyList<string> sourceSentences,
        IEnumerable<string> wordDetailIds,
        IReadOnlyDictionary<string, WordDetail> wordDetails)
    {
        var exampleSentence = ResolveExampleSentence(word, translation, generatedWord, sourceSentences);
        var exampleSentenceTranslation = generatedWord.ExampleSentenceTranslation?.Trim() ?? string.Empty;
        if (!IsUsefulExampleSentence(word, translation, exampleSentence, exampleSentenceTranslation))
        {
            return false;
        }

        var repaired = false;
        foreach (var wordDetailId in wordDetailIds)
        {
            if (!wordDetails.TryGetValue(wordDetailId, out var wordDetail)
                || !ShouldReplaceExampleSentence(
                    word,
                    translation,
                    wordDetail.ExampleSentence,
                    wordDetail.ExampleSentenceTranslation))
            {
                continue;
            }

            wordDetail.ExampleSentence = exampleSentence;
            if (!string.IsNullOrWhiteSpace(exampleSentenceTranslation))
            {
                wordDetail.ExampleSentenceTranslation = exampleSentenceTranslation;
            }

            wordDetail.UpdatedAt = DateTimeOffset.UtcNow;
            repaired = true;
        }

        return repaired;
    }

    private static string? FindSourceSentence(string word, string translation, IReadOnlyList<string> sourceSentences)
    {
        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word)}(?![\p{{L}}\p{{M}}])";
        return sourceSentences
            .Where(sentence => WordPattern.Matches(sentence).Count >= 2)
            .Where(sentence => !LooksLikeGlossLine(word, translation, sentence))
            .FirstOrDefault(sentence => Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase));
    }

    private static bool IsUsefulExampleSentence(string word, string translation, string? exampleSentence, string? exampleSentenceTranslation)
    {
        if (string.IsNullOrWhiteSpace(exampleSentence))
        {
            return false;
        }

        var trimmed = exampleSentence.Trim();
        if (WordPattern.Matches(trimmed).Count < 2)
        {
            return false;
        }

        return ContainsWord(trimmed, word)
            && !HasLearnerNoteArtifacts(trimmed)
            && !LooksLikeGlossLine(word, translation, trimmed)
            && !LooksLikeUnrelatedStockTranslation(trimmed, exampleSentenceTranslation)
            && (string.IsNullOrWhiteSpace(exampleSentenceTranslation)
                || !string.Equals(trimmed, exampleSentenceTranslation.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private static bool ShouldReplaceExampleSentence(
        string word,
        string translation,
        string? exampleSentence,
        string? exampleSentenceTranslation)
    {
        return string.IsNullOrWhiteSpace(exampleSentence)
            || !IsUsefulExampleSentence(word, translation, exampleSentence, exampleSentenceTranslation);
    }

    private static bool HasLearnerNoteArtifacts(string value)
    {
        return Regex.IsMatch(value, @"[()[\]{}\\/|]")
            || Regex.IsMatch(
                value,
                @"(?<![\p{L}\p{M}])(?:m/f|f/m|m\.?/f\.?|f\.?/m\.?|masc(?:uline)?|fem(?:inine)?|neut(?:er)?|sg|pl|singular|plural|pol|eng|en|inf)(?![\p{L}\p{M}])\.?",
                RegexOptions.IgnoreCase)
            || Regex.IsMatch(
                value,
                @"(?<![\p{L}\p{M}])(?:pronunciation|pronounciation|pronounced|pronounce|sounds like|say it like|phonetic|ipa)(?![\p{L}\p{M}])",
                RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeUnrelatedStockTranslation(string exampleSentence, string? exampleSentenceTranslation)
    {
        if (string.IsNullOrWhiteSpace(exampleSentenceTranslation))
        {
            return false;
        }

        var normalizedTranslation = Regex.Replace(exampleSentenceTranslation.Trim(), @"\s+", " ");
        if (!string.Equals(normalizedTranslation.TrimEnd('?'), "Is that true", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return !Regex.IsMatch(exampleSentence, @"(?<![\p{L}\p{M}])(?:prawda|naprawdę|rzeczywiście)(?![\p{L}\p{M}])", RegexOptions.IgnoreCase);
    }

    private static bool ContainsWord(string sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{M}}])";
        return Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase);
    }

    private static bool LooksLikeGlossLine(string word, string translation, string sentence)
    {
        var sentenceTokens = WordPattern.Matches(sentence)
            .Select(match => match.Value)
            .ToList();
        var translationTokens = WordPattern.Matches(translation)
            .Select(match => match.Value)
            .Where(token => !string.Equals(token, word, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (sentenceTokens.Count < 3 || translationTokens.Count == 0 || !ContainsWord(sentence, word))
        {
            return false;
        }

        var overlap = translationTokens.Count(token =>
            sentenceTokens.Contains(token, StringComparer.OrdinalIgnoreCase));
        if (overlap >= 2)
        {
            return true;
        }

        var wordIndex = sentenceTokens.FindIndex(token =>
            string.Equals(token, word, StringComparison.OrdinalIgnoreCase));
        return overlap == 1
            && wordIndex > 0
            && !Regex.IsMatch(sentence.Trim(), @"[.!?]$")
            && sentenceTokens.Count <= translationTokens.Count + 3;
    }

    private static bool IsPronunciationHint(string translation)
    {
        return Regex.IsMatch(
            translation,
            @"(?<![\p{L}\p{M}])(pronunciation|pronounciation|pronounced|pronounce|sounds like|say it like|phonetic|ipa)(?![\p{L}\p{M}])",
            RegexOptions.IgnoreCase);
    }

    private async Task<WordDetail> GetOrCreateWordDetailAsync(
        string sourceLanguage,
        string targetLanguage,
        string word,
        string translation)
    {
        var key = WordDetailKey.Create(sourceLanguage, targetLanguage, word, translation);
        var existing = await _context.WordDetails.FirstOrDefaultAsync(detail => detail.Id == key.Id);
        if (existing != null)
        {
            return existing;
        }

        var now = DateTimeOffset.UtcNow;
        var detail = new WordDetail
        {
            Id = key.Id,
            SourceLanguage = key.SourceLanguage,
            TargetLanguage = key.TargetLanguage,
            Word = key.Word,
            Translation = key.Translation,
            NormalizedWord = key.NormalizedWord,
            NormalizedTranslation = key.NormalizedTranslation,
            NormalizedWordHash = key.NormalizedWordHash,
            NormalizedTranslationHash = key.NormalizedTranslationHash,
            Language = key.TargetLanguage,
            Properties = "{}",
            Variants = "[]",
            Explanation = string.Empty,
            ExampleSentence = string.Empty,
            ExampleSentenceTranslation = string.Empty,
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.WordDetails.Add(detail);
        return detail;
    }
}
