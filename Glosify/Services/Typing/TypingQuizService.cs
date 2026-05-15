using Glosify.Data;
using Microsoft.EntityFrameworkCore;

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

        return await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(_ => Guid.NewGuid())
            .Take(take)
            .Select(item => new TypingWordData
            {
                Id = item.Word.Id,
                Prompt = item.Word.Translation,
                Answer = item.Word.Lemma,
                ExampleSentence = item.Detail == null ? string.Empty : item.Detail.ExampleSentence,
                ExampleTranslation = item.Detail == null ? string.Empty : item.Detail.ExampleSentenceTranslation
            })
            .ToListAsync();
    }
}
