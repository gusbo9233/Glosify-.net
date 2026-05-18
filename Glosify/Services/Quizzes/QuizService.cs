using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class QuizService : IQuizService
{
    private readonly GlosifyContext _context;
    private readonly ILanguageContext _languageContext;

    public QuizService(GlosifyContext context, ILanguageContext languageContext)
    {
        _context = context;
        _languageContext = languageContext;
    }

    public async Task<Quiz?> FindQuizAsync(string userId, Guid? quizId)
    {
        var language = _languageContext.CurrentLanguage;
        var query = _context.Quizzes.Where(q => q.UserId == userId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(q => q.TargetLanguage == language);
        }

        return quizId.HasValue
            ? await query.FirstOrDefaultAsync(q => q.Id == quizId.Value)
            : await query.OrderByDescending(q => q.CreatedAt).FirstOrDefaultAsync();
    }

    public async Task<Quiz?> GetQuizByIdAsync(Guid id, string userId)
    {
        return await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId);
    }

    public async Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId)
    {
        var language = _languageContext.CurrentLanguage;
        var query = _context.Quizzes.Where(q => q.UserId == userId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(q => q.TargetLanguage == language);
        }

        return await query.ToListAsync();
    }

    public async Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId)
    {
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = userId,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Language = targetLanguage,
            CreatedAt = DateTimeOffset.UtcNow,
            ProcessingStatus = "Ready"
        };

        _context.Quizzes.Add(quiz);
        await _context.SaveChangesAsync();

        return quiz;
    }

    public async Task<Quiz?> DeleteQuizAsync(Guid id, string userId)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId);

        if (quiz == null)
            return null;

        var words = await _context.Words
            .Where(word => word.QuizId == quiz.Id)
            .ToListAsync();

        _context.Words.RemoveRange(words);
        await _context.SaveChangesAsync();

        _context.Quizzes.Remove(quiz);
        await _context.SaveChangesAsync();

        return quiz;
    }

    public async Task<bool> UserOwnsQuizAsync(Guid quizId, string userId)
    {
        return await _context.Quizzes.AnyAsync(q => q.Id == quizId && q.UserId == userId);
    }

    public async Task<int> GetAvailableWordCountAsync(Guid quizId)
    {
        return await _context.Words.CountAsync(word => word.QuizId == quizId);
    }
}
