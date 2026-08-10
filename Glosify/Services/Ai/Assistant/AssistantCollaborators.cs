using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantContextResolver(
    GlosifyContext context,
    IBookDocumentService books,
    ILanguageContext languageContext,
    IQuizLanguagePreferenceService languagePreferences)
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
        AssistantTranscriptPageContext? viewedPage,
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

        var resolved = await context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(transcript => transcript.Id == transcriptId.Value
                && transcript.UserId == userId
                && transcript.TargetLanguage == selectedLanguage.Code)
            .Select(transcript => new TranscriptAssistantContext(
                transcript.Id,
                transcript.Title,
                transcript.TargetLanguage,
                transcript.Stream,
                transcript.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Source),
                transcript.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
                null,
                null))
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new InvalidOperationException("That saved transcript was not found.");

        // A page the user is not on is worse than no page at all, so a page number that
        // cannot be real is dropped rather than repeated to the model.
        return viewedPage is null || viewedPage.Page < 1
            ? resolved
            : resolved with
            {
                ViewedPage = viewedPage.Page,
                ViewedStream = RealtimeTranslationTranscriptService.NormalizeStream(viewedPage.Stream)
                    ?? resolved.Stream,
            };
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
internal sealed record TranscriptAssistantContext(
    Guid Id,
    string Title,
    string TargetLanguage,
    string Stream,
    int SourceSegmentCount,
    int TranslationSegmentCount,
    int? ViewedPage,
    string? ViewedStream);
internal sealed record BookAssistantContext(Guid Id, string Title, int PageCount);
