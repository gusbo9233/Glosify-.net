using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Quizzes;

public class QuizService : IQuizService
{
    private readonly GlosifyContext _context;
    private readonly ILanguageContext _languageContext;
    private readonly IAnkiCollectionService _ankiCollections;

    public QuizService(GlosifyContext context, ILanguageContext languageContext, IAnkiCollectionService ankiCollections)
    {
        _context = context;
        _languageContext = languageContext;
        _ankiCollections = ankiCollections;
    }

    public async Task<Quiz?> FindQuizAsync(string userId, Guid? quizId, CancellationToken cancellationToken = default)
    {
        var language = _languageContext.CurrentLanguage;
        var query = _context.Quizzes.AsNoTracking().Where(q => q.UserId == userId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(q => q.TargetLanguage == language);
        }

        return quizId.HasValue
            ? await query.FirstOrDefaultAsync(q => q.Id == quizId.Value, cancellationToken)
            : await query.OrderByDescending(q => q.CreatedAt).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Quiz?> GetQuizByIdAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, cancellationToken);
    }

    public async Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId, CancellationToken cancellationToken = default)
    {
        var language = _languageContext.CurrentLanguage;
        var query = _context.Quizzes.AsNoTracking().Where(q => q.UserId == userId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(q => q.TargetLanguage == language);
        }

        return await query.ToListAsync(cancellationToken);
    }

    public async Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId, Guid? collectionId = null, CancellationToken cancellationToken = default)
    {
        if (QuizLanguageCatalog.IsFreestyle(targetLanguage))
        {
            sourceLanguage = QuizLanguageCatalog.FreestyleName;
            targetLanguage = QuizLanguageCatalog.FreestyleName;
        }

        if (collectionId.HasValue)
        {
            var collectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == targetLanguage, cancellationToken);

            if (!collectionExists)
            {
                throw new QuizCollectionNotFoundException();
            }
        }

        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = name,
            UserId = userId,
            CollectionId = collectionId,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            Language = targetLanguage,
            CreatedAt = DateTimeOffset.UtcNow,
        };

        _context.Quizzes.Add(quiz);
        await _context.SaveChangesAsync(cancellationToken);

        return quiz;
    }

    public async Task<Quiz?> DeleteQuizAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, cancellationToken);

        if (quiz == null)
            return null;

        await _ankiCollections.RetireQuizAsync(quiz.Id, cancellationToken);

        var words = await _context.Words
            .Where(word => word.QuizId == quiz.Id)
            .ToListAsync(cancellationToken);
        var assistantThreads = await _context.AssistantThreads
            .Where(thread => thread.ContextQuizId == quiz.Id)
            .ToListAsync(cancellationToken);
        var assistantMessages = await _context.AssistantMessages
            .Where(message => message.ContextQuizId == quiz.Id)
            .ToListAsync(cancellationToken);

        foreach (var thread in assistantThreads)
        {
            thread.ContextQuizId = null;
        }

        foreach (var message in assistantMessages)
        {
            message.ContextQuizId = null;
        }

        _context.Words.RemoveRange(words);
        _context.Quizzes.Remove(quiz);

        // Save reference cleanup and both deletions together so a constraint
        // failure cannot leave the quiz behind after its words are removed.
        await _context.SaveChangesAsync(cancellationToken);
        return quiz;
    }

    public async Task<bool> UserOwnsQuizAsync(Guid quizId, string userId, CancellationToken cancellationToken = default)
    {
        return await _context.Quizzes.AnyAsync(q => q.Id == quizId && q.UserId == userId, cancellationToken);
    }

    public async Task<int> GetAvailableWordCountAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        return await _context.Words.CountAsync(word => word.QuizId == quizId, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<Guid, int>> GetWordCountsAsync(IReadOnlyCollection<Guid> quizIds, CancellationToken cancellationToken = default)
    {
        if (quizIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await _context.Words
            .Where(word => quizIds.Contains(word.QuizId))
            .GroupBy(word => word.QuizId)
            .Select(group => new { QuizId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.QuizId, group => group.Count, cancellationToken);
    }

    public async Task<int> GetAvailableSentenceCountAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        return await _context.QuizSentences.CountAsync(sentence => sentence.QuizId == quizId, cancellationToken);
    }

}
