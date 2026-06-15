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

    public async Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId, Guid? collectionId = null)
    {
        if (collectionId.HasValue)
        {
            var collectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == targetLanguage);

            if (!collectionExists)
            {
                throw new InvalidOperationException("Collection not found for this user and language.");
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

    public async Task<bool> SetQuizPublicAsync(Guid id, string userId, bool isPublic)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId);

        if (quiz == null)
        {
            return false;
        }

        quiz.IsPublic = isPublic;
        await _context.SaveChangesAsync();
        return true;
    }

    public async Task<IReadOnlyList<Quiz>> GetPublicQuizzesAsync(string language)
    {
        language = language.Trim();

        var publicCollectionIds = await GetPublicCollectionTreeIdsAsync(language);

        return await _context.Quizzes
            .Where(q => q.IsPublic
                && q.TargetLanguage == language
                && (!q.CollectionId.HasValue || !publicCollectionIds.Contains(q.CollectionId.Value)))
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync();
    }

    public async Task<Quiz?> CopyPublicQuizAsync(Guid id, string userId, Guid? collectionId = null)
    {
        var source = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id);

        if (source == null || !await IsQuizPubliclyReadableAsync(source))
        {
            return null;
        }

        if (collectionId.HasValue)
        {
            var targetCollectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == source.TargetLanguage);

            if (!targetCollectionExists)
            {
                return null;
            }
        }

        var copy = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = source.Name,
            UserId = userId,
            CollectionId = collectionId,
            CreatedAt = DateTimeOffset.UtcNow,
            IsSongQuiz = source.IsSongQuiz,
            ProcessingStatus = source.ProcessingStatus,
            ProcessingMessage = source.ProcessingMessage,
            SourceLanguage = source.SourceLanguage,
            TargetLanguage = source.TargetLanguage,
            Language = source.Language,
            AnkiTrackingEnabled = source.AnkiTrackingEnabled,
            AnkiTrackWordsForward = source.AnkiTrackWordsForward,
            AnkiTrackWordsReverse = source.AnkiTrackWordsReverse,
            AnkiTrackSentencesForward = source.AnkiTrackSentencesForward,
            AnkiTrackSentencesReverse = source.AnkiTrackSentencesReverse,
            IsPublic = false,
            OriginalQuizId = source.Id
        };

        var words = await _context.Words
            .Where(word => word.QuizId == source.Id)
            .ToListAsync();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == source.Id)
            .ToListAsync();

        _context.Quizzes.Add(copy);
        _context.Words.AddRange(words.Select(word => new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = copy.Id,
            Lemma = word.Lemma,
            Translation = word.Translation
        }));
        _context.QuizSentences.AddRange(sentences.Select(sentence => new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = copy.Id,
            Text = sentence.Text,
            Translation = sentence.Translation,
            CreatedAt = DateTimeOffset.UtcNow
        }));

        await _context.SaveChangesAsync();
        return copy;
    }


    public async Task<bool> UserOwnsQuizAsync(Guid quizId, string userId)
    {
        return await _context.Quizzes.AnyAsync(q => q.Id == quizId && q.UserId == userId);
    }

    public async Task<int> GetAvailableWordCountAsync(Guid quizId)
    {
        return await _context.Words.CountAsync(word => word.QuizId == quizId);
    }

    private async Task<bool> IsQuizPubliclyReadableAsync(Quiz quiz)
    {
        if (quiz.IsPublic)
        {
            return true;
        }

        if (!quiz.CollectionId.HasValue)
        {
            return false;
        }

        var collection = await _context.Collections
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == quiz.CollectionId.Value);

        while (collection != null)
        {
            if (collection.IsPublic)
            {
                return true;
            }

            if (!collection.ParentCollectionId.HasValue)
            {
                return false;
            }

            collection = await _context.Collections
                .AsNoTracking()
                .FirstOrDefaultAsync(c => c.Id == collection.ParentCollectionId.Value);
        }

        return false;
    }

    private async Task<List<Guid>> GetPublicCollectionTreeIdsAsync(string language)
    {
        var collections = await _context.Collections
            .Where(c => c.Language == language)
            .Select(c => new CollectionVisibilityNode(c.Id, c.ParentCollectionId, c.IsPublic))
            .ToListAsync();

        var childrenByParent = collections
            .Where(c => c.ParentCollectionId.HasValue)
            .GroupBy(c => c.ParentCollectionId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(c => c.Id).ToList());

        var result = new HashSet<Guid>();
        var frontier = collections
            .Where(c => c.IsPublic)
            .Select(c => c.Id)
            .ToList();

        while (frontier.Count > 0)
        {
            var current = frontier[^1];
            frontier.RemoveAt(frontier.Count - 1);

            if (!result.Add(current))
            {
                continue;
            }

            if (childrenByParent.TryGetValue(current, out var childIds))
            {
                frontier.AddRange(childIds);
            }
        }

        return result.ToList();
    }

    private sealed record CollectionVisibilityNode(Guid Id, Guid? ParentCollectionId, bool IsPublic);
}
