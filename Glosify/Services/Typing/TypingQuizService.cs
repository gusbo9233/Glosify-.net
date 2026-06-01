using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public class TypingQuizService : ITypingQuizService
{
    private readonly GlosifyContext _context;

    public TypingQuizService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<TypingQuizData> GetQuizDataAsync(Guid quizId, int wordCount)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == quizId);

        if (quiz == null)
        {
            return new TypingQuizData
            {
                QuizId = quizId,
                QuizName = string.Empty,
                Words = []
            };
        }

        var words = await LoadWordsAsync(quizId, wordCount);

        return new TypingQuizData
        {
            QuizId = quiz.Id,
            QuizName = quiz.Name,
            SourceLanguage = quiz.SourceLanguage,
            TargetLanguage = quiz.TargetLanguage,
            Words = words
        };
    }

    public bool CheckAnswer(string userAnswer, string correctAnswer)
    {
        return userAnswer.Trim()
            .Equals(correctAnswer.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private async Task<IReadOnlyList<TypingWordData>> LoadWordsAsync(Guid quizId, int wordCount)
    {
        var take = Math.Clamp(wordCount, 1, 100);

        var rows = await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();

        return rows
            .Select(item =>
            {
                var sentence = ChooseSentenceForWord(item.Word.Lemma, sentences);
                return new TypingWordData
                {
                    Id = item.Word.Id,
                    Prompt = item.Word.Translation,
                    Answer = item.Word.Lemma,
                    ExampleSentence = sentence?.Text ?? item.Detail?.ExampleSentence ?? string.Empty,
                    ExampleTranslation = sentence?.Translation ?? item.Detail?.ExampleSentenceTranslation ?? string.Empty
                };
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
