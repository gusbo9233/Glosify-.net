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
    private readonly IAiWordGenerationService _aiWordGenerationService;
    private readonly ILogger<GeneratedVocabularyService> _logger;

    public GeneratedVocabularyService(
        GlosifyContext context,
        IQuizService quizService,
        IAiWordGenerationService aiWordGenerationService,
        ILogger<GeneratedVocabularyService> logger)
    {
        _context = context;
        _quizService = quizService;
        _aiWordGenerationService = aiWordGenerationService;
        _logger = logger;
    }

    public async Task<GeneratedVocabularyResult> GenerateAndAddWordsAsync(Guid quizId, string userId, string input)
    {
        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
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
            generatedWords = await _aiWordGenerationService.GenerateWordsFromTextAsync(
                cleanedInput,
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                sourceSentences);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "AI vocabulary generation returned an unexpected response for quiz {QuizId}", quizId);
            return GeneratedVocabularyResult.Failure("The assistant returned an unexpected response. Try a shorter text sample.");
        }

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
                || IsPronunciationHint(translation)
                || existing.Contains(trimmedLemma))
            {
                continue;
            }

            var aiExampleSentence = ResolveExampleSentence(trimmedLemma, translation, generatedWord, sourceSentences);
            var aiExampleSentenceTranslation = generatedWord.ExampleSentenceTranslation?.Trim() ?? string.Empty;
            if (!IsUsefulExampleSentence(trimmedLemma, translation, aiExampleSentence, aiExampleSentenceTranslation))
            {
                aiExampleSentence = string.Empty;
                aiExampleSentenceTranslation = string.Empty;
            }

            var wordDetail = await GetOrCreateWordDetailAsync(
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                trimmedLemma,
                translation);
            if (string.IsNullOrWhiteSpace(wordDetail.ExampleSentence) && !string.IsNullOrWhiteSpace(aiExampleSentence))
            {
                wordDetail.ExampleSentence = aiExampleSentence;
                wordDetail.UpdatedAt = DateTimeOffset.UtcNow;
            }
            if (string.IsNullOrWhiteSpace(wordDetail.ExampleSentenceTranslation) && !string.IsNullOrWhiteSpace(aiExampleSentenceTranslation))
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

        if (added == 0)
        {
            return GeneratedVocabularyResult.Success(0, "No new words were added. The generated words may already be in this quiz.");
        }

        await _context.SaveChangesAsync();
        return GeneratedVocabularyResult.Success(added, $"Added {added} generated {(added == 1 ? "word" : "words")}.");
    }

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
            && !LooksLikeGlossLine(word, translation, trimmed)
            && (string.IsNullOrWhiteSpace(exampleSentenceTranslation)
                || !string.Equals(trimmed, exampleSentenceTranslation.Trim(), StringComparison.OrdinalIgnoreCase));
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
