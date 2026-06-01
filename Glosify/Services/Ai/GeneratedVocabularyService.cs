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
    private readonly IVocabularyGenerationService _vocabularyGenerator;
    private readonly IWordDetailService _wordDetailService;
    private readonly ILogger<GeneratedVocabularyService> _logger;

    public GeneratedVocabularyService(
        GlosifyContext context,
        IQuizService quizService,
        IVocabularyGenerationService vocabularyGenerator,
        IWordDetailService wordDetailService,
        ILogger<GeneratedVocabularyService> logger)
    {
        _context = context;
        _quizService = quizService;
        _vocabularyGenerator = vocabularyGenerator;
        _wordDetailService = wordDetailService;
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

        GeneratedVocabularyBatch generatedVocabulary;
        IReadOnlyList<string> sourceSentences;
        try
        {
            var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
            sourceSentences = ExtractSourceSentences(cleanedInput);
            generatedVocabulary = await _vocabularyGenerator.GenerateWordsFromTextAsync(
                cleanedInput,
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                quiz.Name);
            _logger.LogInformation(
                "Generated vocabulary for quiz {QuizId}: cleaned length {CleanedLength}, source sentence count {SourceSentenceCount}, generated item count {GeneratedCount}, generated sentence count {GeneratedSentenceCount}",
                quizId,
                cleanedInput.Length,
                sourceSentences.Count,
                generatedVocabulary.Words.Count,
                generatedVocabulary.Sentences.Count);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI vocabulary generation returned an unexpected response for quiz {QuizId}", quizId);
            if (ex.Message.StartsWith("The assistant", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("could not find", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("No useful vocabulary", StringComparison.OrdinalIgnoreCase))
            {
                return GeneratedVocabularyResult.Failure(ex.Message);
            }

            return GeneratedVocabularyResult.Failure("The assistant returned an unexpected response. Try a shorter text sample.");
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Gemini request failed for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.LlmAssistant);
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogWarning(ex, "Gemini request timed out for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.LlmAssistant);
        }

        List<Word> existingWords;
        try
        {
            existingWords = await _context.Words
                .Where(w => w.QuizId == quizId)
                .ToListAsync();
        }
        catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
        {
            _logger.LogWarning(ex, "Database was not ready while loading existing words for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
        }

        var existing = new HashSet<string>(existingWords.Select(word => word.Lemma), StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var skippedBlank = 0;
        var skippedPronunciation = 0;
        var skippedExisting = 0;

        foreach (var (word, generatedWord) in generatedVocabulary.Words)
        {
            var trimmedWord = word.Trim();
            var translation = generatedWord.Translation?.Trim();

            if (string.IsNullOrWhiteSpace(trimmedWord) || string.IsNullOrWhiteSpace(translation))
            {
                skippedBlank++;
                continue;
            }

            if (IsPronunciationHint(translation))
            {
                skippedPronunciation++;
                continue;
            }

            if (existing.Contains(trimmedWord))
            {
                skippedExisting++;
                continue;
            }

            WordDetail? wordDetail;
            try
            {
                wordDetail = await _wordDetailService.FindCachedAsync(
                    quiz.SourceLanguage,
                    quiz.TargetLanguage,
                    trimmedWord,
                    translation);
            }
            catch (Exception ex) when (ServiceWarmupMessage.IsDatabaseWarmupFailure(ex))
            {
                _logger.LogWarning(ex, "Database was not ready while preparing generated word detail for quiz {QuizId}", quizId);
                return GeneratedVocabularyResult.Failure(ServiceWarmupMessage.Database);
            }
            _context.Words.Add(new Word
            {
                Id = Guid.NewGuid().ToString("N"),
                QuizId = quizId,
                Lemma = trimmedWord,
                Translation = translation,
                WordDetailId = wordDetail?.Id
            });

            existing.Add(trimmedWord);
            added++;
        }

        var addedSentences = await AddQuizSentencesAsync(quizId, generatedVocabulary.Sentences);

        _logger.LogInformation(
            "Generated vocabulary import for quiz {QuizId}: added {AddedCount}, added sentences {AddedSentenceCount}, skipped blank {SkippedBlankCount}, pronunciation {SkippedPronunciationCount}, existing {SkippedExistingCount}",
            quizId,
            added,
            addedSentences,
            skippedBlank,
            skippedPronunciation,
            skippedExisting);

        if (added == 0 && addedSentences == 0)
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
            return GeneratedVocabularyResult.Success(0, $"Added {addedSentences} quiz {(addedSentences == 1 ? "sentence" : "sentences")}.");
        }

        var sentenceMessage = addedSentences > 0
            ? $" and {addedSentences} quiz {(addedSentences == 1 ? "sentence" : "sentences")}"
            : string.Empty;
        return GeneratedVocabularyResult.Success(added, $"Added {added} generated {(added == 1 ? "word" : "words")}{sentenceMessage}.");
    }

    private static IReadOnlyList<string> ExtractSourceSentences(string input)
    {
        return Regex.Split(VocabularyInputCleaner.CleanForVocabulary(input), @"(?<=[.!?])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static bool IsUsefulExampleSentence(string word, string translation, string? exampleSentence, string? exampleSentenceTranslation)
    {
        return IsUsefulExampleSentenceCore(word, translation, exampleSentence, exampleSentenceTranslation, null);
    }

    private static bool IsUsefulExampleSentenceCore(
        string word,
        string translation,
        string? exampleSentence,
        string? exampleSentenceTranslation,
        string? exampleSentenceWord)
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

        var matchingWord = ContainsWord(trimmed, word)
            ? word
            : !string.IsNullOrWhiteSpace(exampleSentenceWord) && ContainsWord(trimmed, exampleSentenceWord)
                ? exampleSentenceWord
                : string.Empty;

        return !string.IsNullOrWhiteSpace(matchingWord)
            && !HasLearnerNoteArtifacts(trimmed)
            && !LooksLikeGlossLine(matchingWord, translation, trimmed)
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

    private async Task<int> AddQuizSentencesAsync(Guid quizId, IReadOnlyList<GeneratedSentence> generatedSentences)
    {
        if (generatedSentences.Count == 0)
        {
            return 0;
        }

        var existingSentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .Select(sentence => sentence.Text)
            .ToListAsync();
        var existing = new HashSet<string>(existingSentences, StringComparer.OrdinalIgnoreCase);
        var added = 0;
        var now = DateTimeOffset.UtcNow;

        foreach (var generated in generatedSentences)
        {
            var text = VocabularyInputCleaner.CleanForVocabulary(generated.Text).Trim();
            var translation = generated.Translation?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(text)
                || WordPattern.Matches(text).Count < 2
                || HasLearnerNoteArtifacts(text)
                || !existing.Add(text))
            {
                continue;
            }

            _context.QuizSentences.Add(new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quizId,
                Text = text,
                Translation = translation,
                CreatedAt = now
            });
            added++;
        }

        return added;
    }

}
