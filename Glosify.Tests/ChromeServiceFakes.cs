using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
using Microsoft.AspNetCore.Http;

namespace Glosify.Tests;

/// <summary>
/// The five services behind the layout's chrome, in one fake. Only the members the chrome
/// actually calls are meaningful; everything else throws so a test that starts depending on
/// something new fails loudly instead of silently reading a default.
/// </summary>
internal abstract class ChromeServicesBase
    : IAiCreditService,
        IQuizService,
        IBookDocumentService,
        IRealtimeTranslationTranscriptService,
        IQuizLanguagePreferenceService
{
    public virtual Task<AiCreditAccountView> GetOrCreateAccountAsync(
        string userId,
        CancellationToken cancellationToken = default) => throw Unused();

    public virtual Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(
        string userId,
        CancellationToken cancellationToken = default) => throw Unused();

    public virtual Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(
        string userId,
        CancellationToken cancellationToken = default) => throw Unused();

    public virtual Task<TranscriptLibraryPage> GetLibraryAsync(
        string userId,
        string quizLanguageCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) => throw Unused();

    public virtual Task<QuizLanguage?> GetSelectedAsync(
        string userId,
        CancellationToken cancellationToken = default) => throw Unused();

    protected static NotSupportedException Unused() =>
        new("This member is not part of the layout chrome under test.");

    // --- Everything below is interface surface the chrome never touches. ---

    Task<IReadOnlyList<AiCreditTransaction>> IAiCreditService.GetRecentTransactionsAsync(string userId, int count, CancellationToken cancellationToken) => throw Unused();
    Task<AiCreditReservation> IAiCreditService.ReserveAsync(AiUsageContext context, string provider, string model, int estimatedTokens, CancellationToken cancellationToken) => throw Unused();
    Task IAiCreditService.CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken) => throw Unused();
    Task IAiCreditService.ReleaseAsync(Guid reservationId, CancellationToken cancellationToken) => throw Unused();
    Task IAiCreditService.GrantAsync(string adminUserId, string targetUserId, int credits, string note, CancellationToken cancellationToken) => throw Unused();

    Task<Quiz?> IQuizService.FindQuizAsync(string userId, Guid? quizId, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz?> IQuizService.GetQuizByIdAsync(Guid id, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz> IQuizService.CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId, Guid? collectionId, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz?> IQuizService.DeleteQuizAsync(Guid id, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<bool> IQuizService.UserOwnsQuizAsync(Guid quizId, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<bool> IQuizService.SetQuizPublicAsync(Guid id, string userId, bool isPublic, CancellationToken cancellationToken) => throw Unused();
    Task<IReadOnlyList<Quiz>> IQuizService.GetPublicQuizzesAsync(string language, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz?> IQuizService.GetPublicQuizAsync(Guid id, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz?> IQuizService.CopyPublicQuizAsync(Guid id, string userId, Guid? collectionId, CancellationToken cancellationToken) => throw Unused();
    Task<Quiz?> IQuizService.CopyClassroomQuizAsync(Guid id, Guid classroomId, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<int> IQuizService.GetAvailableWordCountAsync(Guid quizId, CancellationToken cancellationToken) => throw Unused();
    Task<IReadOnlyDictionary<Guid, int>> IQuizService.GetWordCountsAsync(IReadOnlyCollection<Guid> quizIds, CancellationToken cancellationToken) => throw Unused();
    Task<int> IQuizService.GetAvailableSentenceCountAsync(Guid quizId, CancellationToken cancellationToken) => throw Unused();

    Task<BookDocument> IBookDocumentService.UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken) => throw Unused();
    Task<BookDocument?> IBookDocumentService.GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<BookPage?> IBookDocumentService.GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<Stream> IBookDocumentService.OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<bool> IBookDocumentService.DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken) => throw Unused();
    Task<Stream> IBookDocumentService.OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken) => throw Unused();

    Task<TranscriptDetailPage?> IRealtimeTranslationTranscriptService.GetDetailAsync(Guid transcriptId, string userId, string quizLanguageCode, int page, int pageSize, string? stream, CancellationToken cancellationToken) => throw Unused();
    Task<TranscriptTextPage?> IRealtimeTranslationTranscriptService.GetTextPageAsync(Guid transcriptId, string userId, string quizLanguageCode, TranscriptTextPageRequest request, CancellationToken cancellationToken) => throw Unused();
    Task<IReadOnlyList<TranscriptPageSpan>> IRealtimeTranslationTranscriptService.GetPageSpansAsync(Guid transcriptId, string userId, string quizLanguageCode, string? stream, int pageSize, CancellationToken cancellationToken) => throw Unused();
    Task IRealtimeTranslationTranscriptService.RenameAsync(Guid transcriptId, string userId, string quizLanguageCode, string title, CancellationToken cancellationToken) => throw Unused();
    Task IRealtimeTranslationTranscriptService.DeleteAsync(Guid transcriptId, string userId, string quizLanguageCode, CancellationToken cancellationToken) => throw Unused();
    Task IRealtimeTranslationTranscriptService.AppendAsync(Guid sessionId, IReadOnlyList<CapturedTranslationSegment> segments, CancellationToken cancellationToken) => throw Unused();

    Task<QuizLanguage> IQuizLanguagePreferenceService.SetSelectedAsync(string userId, string language, CancellationToken cancellationToken) => throw Unused();
    Task IQuizLanguagePreferenceService.ClearAsync(string userId, CancellationToken cancellationToken) => throw Unused();
}

internal sealed class StubChromeServices : ChromeServicesBase
{
    public override Task<AiCreditAccountView> GetOrCreateAccountAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new AiCreditAccountView(userId, 50, 8, 42, null));

    public override Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Quiz>>(
        [
            new Quiz
            {
                Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
                Name = "Market Polish",
                SourceLanguage = "English",
                TargetLanguage = "Polish",
            },
        ]);

    public override Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<BookDocument>>(
        [
            new BookDocument
            {
                Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
                Title = "Tatry Guide",
            },
        ]);

    public override Task<QuizLanguage?> GetSelectedAsync(
        string userId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(QuizLanguageCatalog.Find("pl"));

    public override Task<TranscriptLibraryPage> GetLibraryAsync(
        string userId,
        string quizLanguageCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(new TranscriptLibraryPage(
            [
                new TranscriptLibraryItem(
                    Guid.Parse("33333333-3333-3333-3333-333333333333"),
                    "Kraków market",
                    "pl",
                    RealtimeTranslationTranscriptStreams.Translation,
                    DateTimeOffset.UnixEpoch,
                    DateTimeOffset.UnixEpoch,
                    4),
            ],
            page,
            pageSize,
            1));
}

/// <summary>
/// Every lookup the chrome makes fails. Inherits the base's throwing members unchanged.
/// </summary>
internal sealed class ThrowingChromeServices : ChromeServicesBase;
