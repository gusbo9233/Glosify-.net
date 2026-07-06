using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Language;

namespace Glosify.Services.Quizzes;

public class QuizService : IQuizService
{
    private readonly GlosifyContext _context;
    private readonly ILanguageContext _languageContext;
    private readonly CollectionVisibility _collectionVisibility;

    public QuizService(GlosifyContext context, ILanguageContext languageContext)
    {
        _context = context;
        _languageContext = languageContext;
        _collectionVisibility = new CollectionVisibility(context);
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
        var assistantThreads = await _context.AssistantThreads
            .Where(thread => thread.ContextQuizId == quiz.Id)
            .ToListAsync();
        var assistantMessages = await _context.AssistantMessages
            .Where(message => message.ContextQuizId == quiz.Id)
            .ToListAsync();

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

        var publicCollectionIds = await _collectionVisibility.GetPublicCollectionTreeIdsAsync(language);

        return await _context.Quizzes
            .Where(q => q.IsPublic
                && q.TargetLanguage == language
                && (!q.CollectionId.HasValue || !publicCollectionIds.Contains(q.CollectionId.Value)))
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync();
    }

    public async Task<Quiz?> GetPublicQuizAsync(Guid id)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id);

        return quiz != null && await IsQuizPubliclyReadableAsync(quiz)
            ? quiz
            : null;
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
            Translation = word.Translation,
            CreatedAt = word.CreatedAt
        }));
        _context.QuizSentences.AddRange(sentences.Select(sentence => new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = copy.Id,
            Text = sentence.Text,
            Translation = sentence.Translation,
            CreatedAt = sentence.CreatedAt
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

    public async Task<IReadOnlyDictionary<Guid, int>> GetWordCountsAsync(IReadOnlyCollection<Guid> quizIds)
    {
        if (quizIds.Count == 0)
        {
            return new Dictionary<Guid, int>();
        }

        return await _context.Words
            .Where(word => quizIds.Contains(word.QuizId))
            .GroupBy(word => word.QuizId)
            .Select(group => new { QuizId = group.Key, Count = group.Count() })
            .ToDictionaryAsync(group => group.QuizId, group => group.Count);
    }

    public async Task<int> GetAvailableSentenceCountAsync(Guid quizId)
    {
        return await _context.QuizSentences.CountAsync(sentence => sentence.QuizId == quizId);
    }

    private async Task<bool> IsQuizPubliclyReadableAsync(Quiz quiz)
    {
        if (quiz.IsPublic)
        {
            return true;
        }

        return quiz.CollectionId.HasValue
            && await _collectionVisibility.IsCollectionPubliclyReadableAsync(quiz.CollectionId.Value);
    }
}
