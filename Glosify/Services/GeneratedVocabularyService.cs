using System.Text.RegularExpressions;
using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class GeneratedVocabularyService : IGeneratedVocabularyService
{
    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly IAiWordGenerationService _aiWordGenerationService;
    private readonly IDictionaryEnrichmentQueue _enrichmentQueue;

    public GeneratedVocabularyService(
        GlosifyContext context,
        IQuizService quizService,
        IAiWordGenerationService aiWordGenerationService,
        IDictionaryEnrichmentQueue enrichmentQueue)
    {
        _context = context;
        _quizService = quizService;
        _aiWordGenerationService = aiWordGenerationService;
        _enrichmentQueue = enrichmentQueue;
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
            sourceSentences = ExtractSourceSentences(input);
            generatedWords = await _aiWordGenerationService.GenerateWordsFromTextAsync(
                input,
                quiz.SourceLanguage,
                quiz.TargetLanguage,
                sourceSentences);
        }
        catch (InvalidOperationException)
        {
            return GeneratedVocabularyResult.Failure("The assistant returned an unexpected response. Try a shorter text sample.");
        }

        var existingWords = await _context.Words
            .Where(w => w.QuizId == quizId)
            .Select(w => w.Lemma)
            .ToListAsync();
        var existing = new HashSet<string>(existingWords, StringComparer.OrdinalIgnoreCase);

        var added = 0;
        var enqueued = new List<DictionaryEnrichmentJob>();

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

            var aiExampleSentence = ResolveExampleSentence(trimmedLemma, generatedWord, sourceSentences);
            var aiExplanation = generatedWord.ExampleSentenceTranslation?.Trim() ?? string.Empty;

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
            if (string.IsNullOrWhiteSpace(wordDetail.Explanation) && !string.IsNullOrWhiteSpace(aiExplanation))
            {
                wordDetail.Explanation = aiExplanation;
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

            enqueued.Add(new DictionaryEnrichmentJob(wordDetail.Id, quiz.TargetLanguage, trimmedLemma));
            existing.Add(trimmedLemma);
            added++;
        }

        if (added == 0)
        {
            return GeneratedVocabularyResult.Success(0, "No new words were added. The generated words may already be in this quiz.");
        }

        await _context.SaveChangesAsync();
        foreach (var job in enqueued)
        {
            _enrichmentQueue.Enqueue(job);
        }

        return GeneratedVocabularyResult.Success(added, $"Added {added} generated {(added == 1 ? "word" : "words")}.");
    }

    private static IReadOnlyList<string> ExtractSourceSentences(string input)
    {
        return Regex.Split(input, @"(?<=[.!?])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static string ResolveExampleSentence(
        string word,
        GeneratedWord generatedWord,
        IReadOnlyList<string> sourceSentences)
    {
        var sourceSentence = FindSourceSentence(word, sourceSentences);
        return !string.IsNullOrWhiteSpace(sourceSentence) ? sourceSentence : generatedWord.ExampleSentence?.Trim() ?? string.Empty;
    }

    private static string? FindSourceSentence(string word, IReadOnlyList<string> sourceSentences)
    {
        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word)}(?![\p{{L}}\p{{M}}])";
        return sourceSentences.FirstOrDefault(sentence => Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase));
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
            CreatedAt = now,
            UpdatedAt = now
        };

        _context.WordDetails.Add(detail);
        return detail;
    }
}
