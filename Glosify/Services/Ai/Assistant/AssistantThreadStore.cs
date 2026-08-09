using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal static class AssistantThreadDefaults
{
    public const string NewChatTitle = "New chat";
}

internal sealed class AssistantThreadStore(
    GlosifyContext context,
    IAssistantContextResolver contextResolver,
    IAssistantMessagePresenter presenter) : IAssistantThreadStore
{
    public async Task<IReadOnlyList<AssistantChatSummary>> ListAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var language = await contextResolver.ResolveLanguageAsync(userId, cancellationToken);
        var query = context.AssistantThreads
            .Where(thread => thread.UserId == userId && thread.QuizId == null);
        if (language is not null)
        {
            query = query.Where(thread => thread.Language == language);
        }

        var threads = await query
            .OrderByDescending(thread => thread.UpdatedAt)
            .ToListAsync(cancellationToken);

        return await BuildChatSummariesAsync(threads, cancellationToken);
    }

    public async Task<AssistantChatSummary> CreateAsync(
        string userId,
        Guid? quizId,
        Guid? transcriptId,
        Guid? bookId,
        CancellationToken cancellationToken)
    {
        await contextResolver.ResolveQuizAsync(quizId, userId, cancellationToken);
        await contextResolver.ResolveTranscriptAsync(transcriptId, userId, cancellationToken);
        await contextResolver.ResolveBookAsync(bookId, userId, cancellationToken);

        var now = DateTimeOffset.UtcNow;
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = null,
            ContextQuizId = quizId,
            ContextTranscriptId = transcriptId,
            ContextBookDocumentId = bookId,
            UserId = userId,
            Language = await contextResolver.ResolveLanguageAsync(userId, cancellationToken),
            Title = AssistantThreadDefaults.NewChatTitle,
            CreatedAt = now,
            UpdatedAt = now,
        };

        context.AssistantThreads.Add(thread);
        await context.SaveChangesAsync(cancellationToken);

        return (await BuildChatSummariesAsync([thread], cancellationToken)).Single();
    }

    public async Task<AssistantChatSummary> UpdateAsync(
        Guid threadId,
        string userId,
        string? title,
        Guid? quizId,
        bool updateContext,
        Guid? transcriptId,
        Guid? bookId,
        CancellationToken cancellationToken)
    {
        var thread = await GetOwnedAsync(threadId, userId, cancellationToken);

        if (title is not null)
        {
            thread.Title = presenter.NormalizeTitle(title);
        }

        // The caller supplies the complete desired context. An omitted field therefore
        // clears the stored value instead of silently retaining stale context.
        if (updateContext)
        {
            await contextResolver.ResolveQuizAsync(quizId, userId, cancellationToken);
            await contextResolver.ResolveTranscriptAsync(transcriptId, userId, cancellationToken);
            await contextResolver.ResolveBookAsync(bookId, userId, cancellationToken);
            thread.ContextQuizId = quizId;
            thread.ContextTranscriptId = transcriptId;
            thread.ContextBookDocumentId = bookId;
        }

        thread.UpdatedAt = DateTimeOffset.UtcNow;
        await context.SaveChangesAsync(cancellationToken);

        return (await BuildChatSummariesAsync([thread], cancellationToken)).Single();
    }

    public async Task DeleteAsync(Guid threadId, string userId, CancellationToken cancellationToken)
    {
        var thread = await GetOwnedAsync(threadId, userId, cancellationToken);
        context.AssistantThreads.Remove(thread);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<AssistantHistory> GetChatHistoryAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken)
    {
        var thread = await GetOwnedAsync(threadId, userId, cancellationToken);
        return await BuildHistoryAsync(thread, cancellationToken);
    }

    public async Task<AssistantHistory> GetQuizHistoryAsync(
        Guid quizId,
        string userId,
        CancellationToken cancellationToken)
    {
        var thread = await GetOrCreateDefaultAsync(userId, quizId, cancellationToken);
        return await BuildHistoryAsync(thread, cancellationToken);
    }

    public async Task<AssistantHistory> GetGlobalHistoryAsync(
        string userId,
        CancellationToken cancellationToken)
    {
        var thread = await GetOrCreateDefaultAsync(userId, null, cancellationToken);
        return await BuildHistoryAsync(thread, cancellationToken);
    }

    public async Task<AssistantThread> GetOrCreateDefaultAsync(
        string userId,
        Guid? quizId,
        CancellationToken cancellationToken)
    {
        await contextResolver.ResolveQuizAsync(quizId, userId, cancellationToken);

        // Only chats in the selected language can be resumed. A language change starts
        // a fresh thread rather than mixing contexts from two learning languages.
        var language = await contextResolver.ResolveLanguageAsync(userId, cancellationToken);
        var query = context.AssistantThreads
            .Where(thread => thread.UserId == userId && thread.QuizId == null);
        if (language is not null)
        {
            query = query.Where(thread => thread.Language == language);
        }

        var thread = await query
            .OrderByDescending(candidate => candidate.UpdatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (thread is not null)
        {
            if (quizId != thread.ContextQuizId)
            {
                thread.ContextQuizId = quizId;
                thread.UpdatedAt = DateTimeOffset.UtcNow;
            }

            return thread;
        }

        var now = DateTimeOffset.UtcNow;
        thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            QuizId = null,
            ContextQuizId = quizId,
            UserId = userId,
            Language = language,
            Title = AssistantThreadDefaults.NewChatTitle,
            CreatedAt = now,
            UpdatedAt = now,
        };
        context.AssistantThreads.Add(thread);
        return thread;
    }

    public async Task<AssistantThread> GetOwnedAsync(
        Guid threadId,
        string userId,
        CancellationToken cancellationToken)
    {
        var thread = await context.AssistantThreads
            .FirstOrDefaultAsync(candidate => candidate.Id == threadId
                && candidate.UserId == userId
                && candidate.QuizId == null, cancellationToken)
            ?? throw new InvalidOperationException("Chat not found.");

        // The chat list has already omitted this thread, so this request came from a
        // page that remained open while the selected language changed.
        var language = await contextResolver.ResolveLanguageAsync(userId, cancellationToken);
        if (language is not null && thread.Language != language)
        {
            throw new InvalidOperationException(
                $"That chat belongs to another language. Reload the page to start a {language} chat.");
        }

        return thread;
    }

    public async Task<List<AssistantMessage>> LoadMessagesAsync(
        Guid threadId,
        CancellationToken cancellationToken)
    {
        return await context.AssistantMessages
            .AsNoTracking()
            .Where(message => message.ThreadId == threadId)
            .OrderBy(message => message.Sequence)
            .ToListAsync(cancellationToken);
    }

    private async Task<AssistantHistory> BuildHistoryAsync(
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        var messages = await LoadMessagesAsync(thread.Id, cancellationToken);
        return new AssistantHistory(thread.Id, await MapMessageViewsAsync(messages, cancellationToken));
    }

    private async Task<IReadOnlyList<AssistantChatSummary>> BuildChatSummariesAsync(
        IReadOnlyList<AssistantThread> threads,
        CancellationToken cancellationToken)
    {
        if (threads.Count == 0)
        {
            return [];
        }

        // Tool call/response messages occur in short bursts and each assistant turn
        // finishes with text, so a small recent window is sufficient for list previews.
        var threadIds = threads.Select(thread => thread.Id).ToList();
        var recentByThread = await context.AssistantThreads
            .AsNoTracking()
            .Where(thread => threadIds.Contains(thread.Id))
            .Select(thread => new
            {
                thread.Id,
                Recent = context.AssistantMessages
                    .Where(message => message.ThreadId == thread.Id)
                    .OrderByDescending(message => message.Sequence)
                    .Take(8)
                    .ToList()
            })
            .ToListAsync(cancellationToken);

        var latestByThread = recentByThread
            .ToDictionary(entry => entry.Id, entry => entry.Recent.FirstOrDefault(presenter.HasVisibleContent));

        var quizIds = threads
            .Select(thread => thread.ContextQuizId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var quizNames = quizIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.Quizzes
                .Where(quiz => quizIds.Contains(quiz.Id))
                .ToDictionaryAsync(quiz => quiz.Id, quiz => quiz.Name, cancellationToken);

        var selectedLanguageCode = await contextResolver.ResolveLanguageCodeAsync(
            threads[0].UserId,
            cancellationToken);
        var transcriptIds = threads
            .Select(thread => thread.ContextTranscriptId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var transcriptTitles = transcriptIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.RealtimeTranslationTranscripts
                .Where(transcript => transcriptIds.Contains(transcript.Id)
                    && selectedLanguageCode != null
                    && transcript.TargetLanguage == selectedLanguageCode)
                .ToDictionaryAsync(transcript => transcript.Id, transcript => transcript.Title, cancellationToken);

        var bookIds = threads
            .Select(thread => thread.ContextBookDocumentId)
            .Where(id => id.HasValue)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        var bookTitles = bookIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await context.BookDocuments
                .Where(book => bookIds.Contains(book.Id))
                .ToDictionaryAsync(book => book.Id, book => book.Title, cancellationToken);

        return threads
            .Select(thread =>
            {
                latestByThread.TryGetValue(thread.Id, out var latest);
                var preview = latest is null
                    ? string.Empty
                    : presenter.Truncate(presenter.ExtractVisibleText(latest), 90);
                var quizName = thread.ContextQuizId.HasValue
                    && quizNames.TryGetValue(thread.ContextQuizId.Value, out var name)
                        ? name
                        : null;
                var transcriptTitle = thread.ContextTranscriptId.HasValue
                    && transcriptTitles.TryGetValue(thread.ContextTranscriptId.Value, out var savedTitle)
                        ? savedTitle
                        : null;
                var bookTitle = thread.ContextBookDocumentId.HasValue
                    && bookTitles.TryGetValue(thread.ContextBookDocumentId.Value, out var storedTitle)
                        ? storedTitle
                        : null;
                return new AssistantChatSummary(
                    thread.Id,
                    string.IsNullOrWhiteSpace(thread.Title) ? AssistantThreadDefaults.NewChatTitle : thread.Title,
                    thread.CreatedAt,
                    thread.UpdatedAt,
                    preview,
                    thread.ContextQuizId,
                    quizName,
                    thread.ContextTranscriptId,
                    transcriptTitle,
                    thread.ContextBookDocumentId,
                    bookTitle);
            })
            .ToList();
    }

    private async Task<IReadOnlyList<AssistantMessageView>> MapMessageViewsAsync(
        IReadOnlyList<AssistantMessage> messages,
        CancellationToken cancellationToken)
    {
        var parsed = messages
            .Select(message => (Message: message, Changes: presenter.ParseStoredChanges(message.PendingChangesJson)))
            .ToList();

        // Batch labels once per context quiz instead of issuing a query for each message.
        var emptyLabels = (IReadOnlyDictionary<string, AssistantWordLabel>)new Dictionary<string, AssistantWordLabel>();
        var labelsByQuiz = new Dictionary<Guid, IReadOnlyDictionary<string, AssistantWordLabel>>();
        foreach (var group in parsed
            .Where(entry => entry.Message.ContextQuizId.HasValue && entry.Changes.Count > 0)
            .GroupBy(entry => entry.Message.ContextQuizId!.Value))
        {
            labelsByQuiz[group.Key] = await LoadWordLabelsAsync(
                group.Key,
                group.SelectMany(entry => entry.Changes),
                cancellationToken);
        }

        return parsed
            .Select(entry =>
            {
                var wordLabels = entry.Message.ContextQuizId.HasValue
                    ? labelsByQuiz.GetValueOrDefault(entry.Message.ContextQuizId.Value, emptyLabels)
                    : emptyLabels;
                return new AssistantMessageView(
                    entry.Message.Id,
                    entry.Message.Role,
                    presenter.ExtractVisibleText(entry.Message),
                    [],
                    entry.Changes
                        .Select(change => presenter.PresentPendingChange(change, wordLabels))
                        .ToList(),
                    entry.Message.Status,
                    entry.Message.CreatedAt);
            })
            .ToList();
    }

    private async Task<IReadOnlyDictionary<string, AssistantWordLabel>> LoadWordLabelsAsync(
        Guid quizId,
        IEnumerable<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        var wordIds = presenter.GetReferencedWordIds(changes);
        if (wordIds.Count == 0)
        {
            return new Dictionary<string, AssistantWordLabel>();
        }

        return await context.Words
            .Where(word => word.QuizId == quizId && wordIds.Contains(word.Id))
            .Select(word => new AssistantWordLabel(word.Id, word.Lemma, word.Translation))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
    }
}
