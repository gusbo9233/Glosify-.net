using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Speaking;

public interface ISpeakingQuizReader
{
    Task<IReadOnlyList<SpeakingQuizOption>> ListAsync(
        string userId,
        string language,
        CancellationToken cancellationToken = default);

    Task<SpeakingActiveQuiz?> GetAsync(
        Guid quizId,
        string userId,
        string language,
        CancellationToken cancellationToken = default);

    Task<SpeakingQuizWordPage?> GetWordsAsync(
        Guid quizId,
        string userId,
        string language,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<SpeakingQuizSentencePage?> GetSentencesAsync(
        Guid quizId,
        string userId,
        string language,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);
}

public sealed class SpeakingQuizReader(GlosifyContext context) : ISpeakingQuizReader
{
    public async Task<IReadOnlyList<SpeakingQuizOption>> ListAsync(
        string userId,
        string language,
        CancellationToken cancellationToken = default) =>
        await context.Quizzes
            .AsNoTracking()
            .Where(quiz => quiz.UserId == userId && quiz.TargetLanguage == language)
            .OrderBy(quiz => quiz.Name)
            .ThenBy(quiz => quiz.CreatedAt)
            .Select(quiz => new SpeakingQuizOption(quiz.Id, quiz.Name))
            .ToListAsync(cancellationToken);

    public async Task<SpeakingActiveQuiz?> GetAsync(
        Guid quizId,
        string userId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var quiz = await context.Quizzes
            .AsNoTracking()
            .Where(item =>
                item.Id == quizId
                && item.UserId == userId
                && item.TargetLanguage == language)
            .Select(item => new { item.Id, item.Name })
            .SingleOrDefaultAsync(cancellationToken);
        if (quiz is null)
        {
            return null;
        }

        var wordCount = await context.Words
            .CountAsync(word => word.QuizId == quiz.Id, cancellationToken);
        var sentenceCount = await context.QuizSentences
            .CountAsync(sentence => sentence.QuizId == quiz.Id, cancellationToken);
        return new SpeakingActiveQuiz(quiz.Id, quiz.Name, wordCount, sentenceCount);
    }

    public async Task<SpeakingQuizWordPage?> GetWordsAsync(
        Guid quizId,
        string userId,
        string language,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsQuizAsync(quizId, userId, language, cancellationToken))
        {
            return null;
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var items = await context.Words
            .AsNoTracking()
            .Where(word => word.QuizId == quizId)
            .OrderBy(word => word.CreatedAt)
            .ThenBy(word => word.Id)
            .Skip(normalizedOffset)
            .Take(normalizedLimit + 1)
            .Select(word => new SpeakingQuizWord(word.Id, word.Lemma, word.Translation))
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > normalizedLimit;
        return new SpeakingQuizWordPage(
            items.Take(normalizedLimit).ToArray(),
            hasMore,
            hasMore ? normalizedOffset + normalizedLimit : null);
    }

    public async Task<SpeakingQuizSentencePage?> GetSentencesAsync(
        Guid quizId,
        string userId,
        string language,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsQuizAsync(quizId, userId, language, cancellationToken))
        {
            return null;
        }

        var normalizedOffset = Math.Max(0, offset);
        var normalizedLimit = Math.Clamp(limit, 1, 50);
        var items = await context.QuizSentences
            .AsNoTracking()
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.CreatedAt)
            .ThenBy(sentence => sentence.Id)
            .Skip(normalizedOffset)
            .Take(normalizedLimit + 1)
            .Select(sentence => new SpeakingQuizSentence(
                sentence.Id,
                sentence.Text,
                sentence.Translation))
            .ToListAsync(cancellationToken);
        var hasMore = items.Count > normalizedLimit;
        return new SpeakingQuizSentencePage(
            items.Take(normalizedLimit).ToArray(),
            hasMore,
            hasMore ? normalizedOffset + normalizedLimit : null);
    }

    private Task<bool> OwnsQuizAsync(
        Guid quizId,
        string userId,
        string language,
        CancellationToken cancellationToken) =>
        context.Quizzes.AnyAsync(
            quiz =>
                quiz.Id == quizId
                && quiz.UserId == userId
                && quiz.TargetLanguage == language,
            cancellationToken);
}

public sealed record SpeakingQuizWord(string Id, string Text, string Translation);

public sealed record SpeakingQuizSentence(Guid Id, string Text, string Translation);

public sealed record SpeakingQuizWordPage(
    IReadOnlyList<SpeakingQuizWord> Items,
    bool HasMore,
    int? NextOffset);

public sealed record SpeakingQuizSentencePage(
    IReadOnlyList<SpeakingQuizSentence> Items,
    bool HasMore,
    int? NextOffset);

public sealed class SpeakingQuizContextState(
    string userId,
    string language,
    SpeakingActiveQuiz? activeQuiz = null)
{
    public string UserId { get; } = userId;
    public string Language { get; } = language;
    public SpeakingActiveQuiz? ActiveQuiz { get; set; } = activeQuiz;

    public SpeakingQuizContextState Clone() => new(UserId, Language, ActiveQuiz);
}
