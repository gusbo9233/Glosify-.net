using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Glosify.Services.Ai.Assistant;

internal interface IAssistantContextResolver
{
    Task<string?> ResolveLanguageAsync(string userId, CancellationToken cancellationToken);
    Task<string?> ResolveLanguageCodeAsync(string userId, CancellationToken cancellationToken);
    Task<Quiz?> ResolveQuizAsync(Guid? quizId, string userId, CancellationToken cancellationToken);
    Task<TranscriptAssistantContext?> ResolveTranscriptAsync(Guid? transcriptId, string userId, CancellationToken cancellationToken);
    Task<BookAssistantContext?> ResolveBookAsync(Guid? bookDocumentId, string userId, CancellationToken cancellationToken);
    Task<DocumentPageContext> ResolveDocumentPageAsync(AssistantDocumentContext document, string userId, CancellationToken cancellationToken);
}

internal interface IAssistantMessagePresenter
{
    string NormalizeTitle(string? title);
    string ExtractVisibleText(AssistantMessage message);
    bool HasVisibleContent(AssistantMessage message);
    string Truncate(string? value, int max);
    IReadOnlyList<PendingChange> ParseStoredChanges(string? json);
    IReadOnlyList<string> GetReferencedWordIds(IEnumerable<PendingChange> changes);
    AssistantPendingChangeView PresentPendingChange(
        PendingChange change,
        IReadOnlyDictionary<string, AssistantWordLabel> wordLabels);
}

internal interface IAssistantThreadStore
{
    Task<IReadOnlyList<AssistantChatSummary>> ListAsync(string userId, CancellationToken cancellationToken);
    Task<AssistantChatSummary> CreateAsync(string userId, Guid? quizId, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken);
    Task<AssistantChatSummary> UpdateAsync(Guid threadId, string userId, string? title, Guid? quizId, bool updateContext, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid threadId, string userId, CancellationToken cancellationToken);
    Task<AssistantHistory> GetChatHistoryAsync(Guid threadId, string userId, CancellationToken cancellationToken);
    Task<AssistantHistory> GetQuizHistoryAsync(Guid quizId, string userId, CancellationToken cancellationToken);
    Task<AssistantHistory> GetGlobalHistoryAsync(string userId, CancellationToken cancellationToken);
}

internal interface IAssistantTurnRunner
{
    Task<AssistantTurnResponse> RunChatAsync(Guid threadId, string userId, string message, Guid? quizId, string? focusedWordId, string? model, AssistantDocumentContext? document, Guid? customQuizId, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken);
    Task<AssistantTurnResponse> RunQuizAsync(Guid quizId, string userId, string message, string? focusedWordId, string? model, AssistantDocumentContext? document, CancellationToken cancellationToken);
    Task<AssistantTurnResponse> RunGlobalAsync(string userId, string message, string? model, AssistantDocumentContext? document, CancellationToken cancellationToken);
}

internal interface IAssistantChangeWorkflow
{
    Task<AssistantApplyResult> ApplyAsync(Guid messageId, string userId, CancellationToken cancellationToken);
    Task RejectAsync(Guid messageId, string userId, CancellationToken cancellationToken);
    Task ResetAsync(string userId, CancellationToken cancellationToken);
}

internal sealed class AssistantContextResolver(
    GlosifyContext context,
    IBookDocumentService books,
    ILanguageContext languageContext,
    IQuizLanguagePreferenceService languagePreferences) : IAssistantContextResolver
{
    public async Task<string?> ResolveLanguageAsync(string userId, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(languageContext.CurrentLanguage))
        {
            return languageContext.CurrentLanguage;
        }

        return (await languagePreferences.GetSelectedAsync(userId, cancellationToken))?.Name;
    }

    public async Task<string?> ResolveLanguageCodeAsync(string userId, CancellationToken cancellationToken) =>
        (await languagePreferences.GetSelectedAsync(userId, cancellationToken))?.Code;

    public async Task<Quiz?> ResolveQuizAsync(Guid? quizId, string userId, CancellationToken cancellationToken)
    {
        if (!quizId.HasValue)
        {
            return null;
        }

        return await context.Quizzes
            .AsNoTracking()
            .FirstOrDefaultAsync(quiz => quiz.Id == quizId.Value && quiz.UserId == userId, cancellationToken)
            ?? throw new QuizNotFoundException();
    }

    public async Task<TranscriptAssistantContext?> ResolveTranscriptAsync(
        Guid? transcriptId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!transcriptId.HasValue)
        {
            return null;
        }

        var selectedLanguage = await languagePreferences.GetSelectedAsync(userId, cancellationToken);
        if (selectedLanguage is null)
        {
            throw new InvalidOperationException("Choose a Glosify quiz language before using saved transcripts.");
        }

        return await context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(transcript => transcript.Id == transcriptId.Value
                && transcript.UserId == userId
                && transcript.TargetLanguage == selectedLanguage.Code)
            .Select(transcript => new TranscriptAssistantContext(
                transcript.Id,
                transcript.Title,
                transcript.TargetLanguage,
                transcript.Stream))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("That saved transcript was not found.");
    }

    public async Task<BookAssistantContext?> ResolveBookAsync(
        Guid? bookDocumentId,
        string userId,
        CancellationToken cancellationToken)
    {
        if (!bookDocumentId.HasValue)
        {
            return null;
        }

        var book = await books.GetOwnedDocumentAsync(bookDocumentId.Value, userId, cancellationToken)
            ?? throw new InvalidOperationException("That book was not found.");
        return new BookAssistantContext(book.Id, book.Title, book.PageCount);
    }

    public async Task<DocumentPageContext> ResolveDocumentPageAsync(
        AssistantDocumentContext document,
        string userId,
        CancellationToken cancellationToken)
    {
        if (document.PageNumber < 1)
        {
            throw new InvalidOperationException("Choose a valid book page.");
        }

        var page = await books.GetOwnedPageAsync(
            document.DocumentId,
            document.PageNumber,
            userId,
            cancellationToken)
            ?? throw new InvalidOperationException("That book page was not found.");

        return new DocumentPageContext(
            page.BookDocument.Title,
            page.PageNumber,
            page.Text,
            page.ExtractionWarning);
    }
}

internal sealed record DocumentPageContext(string Title, int PageNumber, string Text, string? Warning);
internal sealed record TranscriptAssistantContext(Guid Id, string Title, string TargetLanguage, string Stream);
internal sealed record BookAssistantContext(Guid Id, string Title, int PageCount);

// These scoped collaborators define the ownership boundaries used by the façade. The
// runtime is deliberately private to the feature and can be decomposed behind these
// seams without changing controllers or saved-chat behavior.
internal sealed class AssistantThreadStore(AssistantRuntime runtime) : IAssistantThreadStore
{
    public Task<IReadOnlyList<AssistantChatSummary>> ListAsync(string userId, CancellationToken cancellationToken) =>
        runtime.ListChatsAsync(userId, cancellationToken);

    public Task<AssistantChatSummary> CreateAsync(string userId, Guid? quizId, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken) =>
        runtime.CreateChatAsync(userId, quizId, cancellationToken, transcriptId, bookId);

    public Task<AssistantChatSummary> UpdateAsync(Guid threadId, string userId, string? title, Guid? quizId, bool updateContext, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken) =>
        runtime.UpdateChatAsync(threadId, userId, title, quizId, updateContext, cancellationToken, transcriptId, bookId);

    public Task DeleteAsync(Guid threadId, string userId, CancellationToken cancellationToken) =>
        runtime.DeleteChatAsync(threadId, userId, cancellationToken);

    public Task<AssistantHistory> GetChatHistoryAsync(Guid threadId, string userId, CancellationToken cancellationToken) =>
        runtime.GetChatHistoryAsync(threadId, userId, cancellationToken);

    public Task<AssistantHistory> GetQuizHistoryAsync(Guid quizId, string userId, CancellationToken cancellationToken) =>
        runtime.GetHistoryAsync(quizId, userId, cancellationToken);

    public Task<AssistantHistory> GetGlobalHistoryAsync(string userId, CancellationToken cancellationToken) =>
        runtime.GetGlobalHistoryAsync(userId, cancellationToken);
}

internal sealed class AssistantTurnRunner(AssistantRuntime runtime) : IAssistantTurnRunner
{
    public Task<AssistantTurnResponse> RunChatAsync(Guid threadId, string userId, string message, Guid? quizId, string? focusedWordId, string? model, AssistantDocumentContext? document, Guid? customQuizId, Guid? transcriptId, Guid? bookId, CancellationToken cancellationToken) =>
        runtime.SendChatMessageAsync(threadId, userId, message, quizId, focusedWordId, model, document, customQuizId, cancellationToken, transcriptId, bookId);

    public Task<AssistantTurnResponse> RunQuizAsync(Guid quizId, string userId, string message, string? focusedWordId, string? model, AssistantDocumentContext? document, CancellationToken cancellationToken) =>
        runtime.SendMessageAsync(quizId, userId, message, focusedWordId, model, document, cancellationToken);

    public Task<AssistantTurnResponse> RunGlobalAsync(string userId, string message, string? model, AssistantDocumentContext? document, CancellationToken cancellationToken) =>
        runtime.SendGlobalMessageAsync(userId, message, model, document, cancellationToken);
}
