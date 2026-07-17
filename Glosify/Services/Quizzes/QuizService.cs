using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Language;
using Glosify.Services.CustomQuizzes;

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
        if (collectionId.HasValue)
        {
            var collectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == targetLanguage, cancellationToken);

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
        await _context.SaveChangesAsync(cancellationToken);

        return quiz;
    }

    public async Task<Quiz?> DeleteQuizAsync(Guid id, string userId, CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, cancellationToken);

        if (quiz == null)
            return null;

        var words = await _context.Words
            .Where(word => word.QuizId == quiz.Id)
            .ToListAsync(cancellationToken);
        // Classroom share links reference quizzes with NoAction FKs, so they must
        // go before the quiz row or SQL Server rejects the delete.
        var classroomLinks = await _context.ClassroomContents
            .Where(link => link.QuizId == quiz.Id)
            .ToListAsync(cancellationToken);
        // Assignments keep their row (title/instructions still matter) but lose
        // the quiz reference, mirroring the assistant-thread context cleanup.
        var assignments = await _context.ClassroomAssignments
            .Where(assignment => assignment.QuizId == quiz.Id)
            .ToListAsync(cancellationToken);
        foreach (var assignment in assignments)
        {
            assignment.QuizId = null;
        }
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
        _context.ClassroomContents.RemoveRange(classroomLinks);
        _context.Quizzes.Remove(quiz);

        // Save reference cleanup and both deletions together so a constraint
        // failure cannot leave the quiz behind after its words are removed.
        await _context.SaveChangesAsync(cancellationToken);

        return quiz;
    }

    public async Task<bool> SetQuizPublicAsync(Guid id, string userId, bool isPublic, CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .FirstOrDefaultAsync(q => q.Id == id && q.UserId == userId, cancellationToken);

        if (quiz == null)
        {
            return false;
        }

        quiz.IsPublic = isPublic;
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<Quiz>> GetPublicQuizzesAsync(string language, CancellationToken cancellationToken = default)
    {
        language = language.Trim();

        var publicCollectionIds = await _collectionVisibility.GetPublicCollectionTreeIdsAsync(language);

        return await _context.Quizzes
            .AsNoTracking()
            .Where(q => q.IsPublic
                && q.TargetLanguage == language
                && (!q.CollectionId.HasValue || !publicCollectionIds.Contains(q.CollectionId.Value)))
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(cancellationToken);
    }

    public async Task<Quiz?> GetPublicQuizAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var quiz = await _context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        return quiz != null && await IsQuizPubliclyReadableAsync(quiz, cancellationToken)
            ? quiz
            : null;
    }

    public async Task<Quiz?> CopyPublicQuizAsync(Guid id, string userId, Guid? collectionId = null, CancellationToken cancellationToken = default)
    {
        var source = await _context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (source == null || !await IsQuizPubliclyReadableAsync(source, cancellationToken))
        {
            return null;
        }

        if (collectionId.HasValue)
        {
            var targetCollectionExists = await _context.Collections.AnyAsync(c =>
                c.Id == collectionId.Value
                && c.UserId == userId
                && c.Language == source.TargetLanguage, cancellationToken);

            if (!targetCollectionExists)
            {
                return null;
            }
        }

        return await CopyQuizCoreAsync(source, userId, collectionId, cancellationToken);
    }

    public async Task<Quiz?> CopyClassroomQuizAsync(Guid id, Guid classroomId, string userId, CancellationToken cancellationToken = default)
    {
        var isMember = await _context.ClassroomMemberships
            .AnyAsync(m => m.ClassroomId == classroomId && m.UserId == userId, cancellationToken);
        var isShared = await _context.ClassroomContents
            .AnyAsync(c => c.ClassroomId == classroomId && c.QuizId == id, cancellationToken);

        if (!isMember || !isShared)
        {
            return null;
        }

        var source = await _context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(q => q.Id == id, cancellationToken);

        if (source == null)
        {
            return null;
        }

        return await CopyQuizCoreAsync(source, userId, collectionId: null, cancellationToken);
    }

    private async Task<Quiz> CopyQuizCoreAsync(Quiz source, string userId, Guid? collectionId, CancellationToken cancellationToken)
    {
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
            .AsNoTracking()
            .Where(word => word.QuizId == source.Id)
            .ToListAsync(cancellationToken);
        var sentences = await _context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == source.Id)
            .ToListAsync(cancellationToken);

        _context.Quizzes.Add(copy);
        var wordIdMap = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var word in words)
        {
            var copiedWordId = Guid.NewGuid().ToString("N");
            wordIdMap[word.Id] = copiedWordId;
            _context.Words.Add(new Word
            {
                Id = copiedWordId,
                QuizId = copy.Id,
                Lemma = word.Lemma,
                Translation = word.Translation,
                CreatedAt = word.CreatedAt
            });
        }
        _context.QuizSentences.AddRange(sentences.Select(sentence => new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = copy.Id,
            Text = sentence.Text,
            Translation = sentence.Translation,
            CreatedAt = sentence.CreatedAt
        }));

        await new CustomQuizService(_context).CloneForCopiedQuizAsync(source.Id, copy.Id, wordIdMap, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
        return copy;
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

    private async Task<bool> IsQuizPubliclyReadableAsync(Quiz quiz, CancellationToken cancellationToken)
    {
        if (quiz.IsPublic)
        {
            return true;
        }

        return quiz.CollectionId.HasValue
            && await _collectionVisibility.IsCollectionPubliclyReadableAsync(quiz.CollectionId.Value);
    }
}
