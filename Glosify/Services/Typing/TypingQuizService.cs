using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Glosify.Services.Typing;

public class TypingQuizService : ITypingQuizService
{
    private readonly GlosifyContext _context;

    public TypingQuizService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<TypingQuizData> GetQuizDataAsync(Guid quizId, int wordCount, string? practiceDirection = null, string? practiceItemType = null, int rangeStartPercent = 0, int rangeEndPercent = 100, IReadOnlyCollection<string>? wordIds = null)
    {
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId);

        if (quiz == null)
        {
            return new TypingQuizData
            {
                QuizId = quizId,
                QuizName = string.Empty,
                PracticeDirection = normalizedDirection,
                PracticeItemType = normalizedItemType,
                Words = []
            };
        }

        var words = wordIds is { Count: > 0 }
            ? await LoadWordsByIdsAsync(quizId, wordIds, normalizedDirection)
            : PracticeItemType.IsSentences(normalizedItemType)
                ? await LoadSentencesAsync(quizId, wordCount, normalizedDirection, rangeStartPercent, rangeEndPercent)
                : await LoadWordsAsync(quizId, wordCount, normalizedDirection, rangeStartPercent, rangeEndPercent);

        return new TypingQuizData
        {
            QuizId = quiz.Id,
            QuizName = quiz.Name,
            SourceLanguage = quiz.SourceLanguage,
            TargetLanguage = quiz.TargetLanguage,
            PracticeDirection = normalizedDirection,
            PromptLanguage = PracticeDirection.PromptLanguage(normalizedDirection, quiz.SourceLanguage, quiz.TargetLanguage),
            AnswerLanguage = PracticeDirection.AnswerLanguage(normalizedDirection, quiz.SourceLanguage, quiz.TargetLanguage),
            PracticeItemType = normalizedItemType,
            Words = words
        };
    }

    public bool CheckAnswer(string userAnswer, string correctAnswer)
    {
        return userAnswer.Trim()
            .Equals(correctAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<TypingWordData>> LoadWordsAsync(Guid quizId, int wordCount, string practiceDirection, int rangeStartPercent = 0, int rangeEndPercent = 100)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        var orderedWords = await _context.Words
            .Where(word => word.QuizId == quizId)
            .OrderBy(word => word.CreatedAt)
            .ThenBy(word => word.Id)
            .ToListAsync();
        var pool = PracticeRange.Slice(orderedWords, rangeStartPercent, rangeEndPercent);

        var rows = pool
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToList();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();

        return rows
            .Select(item =>
            {
                var sentence = ChooseSentenceForWord(item.Lemma, sentences);
                return new TypingWordData
                {
                    Id = item.Id,
                    Prompt = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Translation : item.Lemma,
                    Answer = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Lemma : item.Translation,
                    ExampleSentence = sentence?.Text ?? string.Empty,
                    ExampleTranslation = sentence?.Translation ?? string.Empty
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<TypingWordData>> LoadWordsByIdsAsync(Guid quizId, IReadOnlyCollection<string> wordIds, string practiceDirection)
    {
        var rows = await _context.Words
            .Where(word => word.QuizId == quizId && wordIds.Contains(word.Id))
            .OrderBy(_ => Guid.NewGuid())
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();

        return rows
            .Select(item =>
            {
                var sentence = ChooseSentenceForWord(item.Lemma, sentences);
                return new TypingWordData
                {
                    Id = item.Id,
                    Prompt = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Translation : item.Lemma,
                    Answer = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Lemma : item.Translation,
                    ExampleSentence = sentence?.Text ?? string.Empty,
                    ExampleTranslation = sentence?.Translation ?? string.Empty
                };
            })
            .ToList();
    }

    private async Task<IReadOnlyList<TypingWordData>> LoadSentencesAsync(Guid quizId, int sentenceCount, string practiceDirection, int rangeStartPercent = 0, int rangeEndPercent = 100)
    {
        var take = Math.Clamp(sentenceCount, 1, 100);

        var orderedSentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .ToListAsync();
        var pool = PracticeRange.Slice(orderedSentences, rangeStartPercent, rangeEndPercent);

        var rows = pool
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToList();

        return rows
            .Select(item => new TypingWordData
            {
                Id = item.Id.ToString("N"),
                Prompt = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Translation : item.Text,
                Answer = PracticeDirection.IsSourceToTarget(practiceDirection) ? item.Text : item.Translation,
                ExampleSentence = string.Empty,
                ExampleTranslation = string.Empty
            })
            .ToList();
    }

    private static QuizSentence? ChooseSentenceForWord(string lemma, IReadOnlyList<QuizSentence> sentences)
    {
        return sentences.FirstOrDefault(sentence => ContainsWord(sentence.Text, lemma));
    }

    private static bool ContainsWord(string sentence, string word)
    {
        if (string.IsNullOrWhiteSpace(sentence) || string.IsNullOrWhiteSpace(word))
        {
            return false;
        }

        var pattern = $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(word.Trim())}(?![\p{{L}}\p{{M}}])";
        return Regex.IsMatch(sentence, pattern, RegexOptions.IgnoreCase);
    }
}
