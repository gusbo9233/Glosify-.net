using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
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

    /// <summary>
    /// The language quiz content should be translated into.
    /// </summary>
    /// <remarks>
    /// The selected quiz wins, because changing an existing quiz has to stay consistent with
    /// the translations already in it. Null means genuinely unknown, and only then is asking
    /// the user the right move.
    /// </remarks>
    public async Task<string?> ResolveSourceLanguageAsync(
        Quiz? selectedQuiz,
        string userId,
        AssistantThread? thread,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(selectedQuiz?.SourceLanguage))
        {
            return selectedQuiz.SourceLanguage;
        }

        var preferred = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.PreferredSourceLanguage)
            .SingleOrDefaultAsync(cancellationToken);

        return string.IsNullOrWhiteSpace(preferred)
            ? NullIfBlank(thread?.ConversationLanguage)
            : preferred;
    }

    /// <summary>
    /// The language the assistant should reply in.
    /// </summary>
    /// <remarks>
    /// Always resolves to something. A conversation that has been running in one language is
    /// the strongest evidence available, and a product default beats interrupting the user to
    /// ask a question they have already answered by typing.
    /// </remarks>
    public async Task<string> ResolveReplyLanguageAsync(
        string userId,
        AssistantThread? thread,
        CancellationToken cancellationToken)
    {
        if (NullIfBlank(thread?.ConversationLanguage) is { } conversationLanguage)
        {
            return conversationLanguage;
        }

        var preferred = await context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.PreferredAssistantLanguage)
            .SingleOrDefaultAsync(cancellationToken);

        return NullIfBlank(preferred) ?? DefaultReplyLanguage;
    }

    /// <summary>
    /// The reply language a new chat starts in, taken from the UI the user is reading.
    /// </summary>
    public string ResolveInitialConversationLanguage()
    {
        // The neutral parent, so en-GB and en-US both read as "English" rather than as two
        // different reply languages. The invariant culture names nothing and falls through.
        var culture = CultureInfo.CurrentUICulture;
        if (string.IsNullOrEmpty(culture.Name))
        {
            return DefaultReplyLanguage;
        }

        try
        {
            return new CultureInfo(culture.TwoLetterISOLanguageName).EnglishName;
        }
        catch (CultureNotFoundException)
        {
            return DefaultReplyLanguage;
        }
    }

    private const string DefaultReplyLanguage = "English";

    private static string? NullIfBlank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

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

        // A page the user is not on is worse than no page at all: telling the model "the
        // user is reading page 7" when they are not makes "this page" resolve to text they
        // never saw. The page number arrives from the client, so it is checked against the
        // chosen stream's real length and dropped — not clamped — when it cannot be real.
        if (viewedPage is null)
        {
            return resolved;
        }
        var viewedStream = RealtimeTranslationTranscriptService.NormalizeStream(viewedPage.Stream)
            ?? resolved.Stream;
        var segments = viewedStream == RealtimeTranslationTranscriptStreams.Translation
            ? resolved.TranslationSegmentCount
            : resolved.SourceSegmentCount;
        var pages = (int)Math.Ceiling(
            segments / (double)RealtimeTranslationTranscriptService.DetailPageSize);
        return viewedPage.Page < 1 || viewedPage.Page > pages
            ? resolved
            : resolved with { ViewedPage = viewedPage.Page, ViewedStream = viewedStream };
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
