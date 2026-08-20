using Glosify.Models.Entities;

namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationTranscriptService
{
    Task<TranscriptLibraryPage> GetLibraryAsync(string userId, string quizLanguageCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<TranscriptDetailPage?> GetDetailAsync(Guid transcriptId, string userId, string quizLanguageCode, int page, int pageSize, string? stream = null, CancellationToken cancellationToken = default);
    Task<TranscriptTextPage?> GetTextPageAsync(Guid transcriptId, string userId, string quizLanguageCode, TranscriptTextPageRequest request, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<TranscriptPageSpan>> GetPageSpansAsync(Guid transcriptId, string userId, string quizLanguageCode, string? stream, int pageSize, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid transcriptId, string userId, string quizLanguageCode, string title, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid transcriptId, string userId, string quizLanguageCode, CancellationToken cancellationToken = default);
    Task AppendAsync(Guid sessionId, IReadOnlyList<CapturedTranslationSegment> segments, CancellationToken cancellationToken = default);
}

public sealed record TranscriptLibraryItem(
    Guid Id,
    string Title,
    string TargetLanguage,
    string Stream,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int SegmentCount);

public sealed record TranscriptLibraryPage(
    IReadOnlyList<TranscriptLibraryItem> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record TranscriptDetailPage(
    Guid Id,
    string Title,
    string TargetLanguage,
    string Stream,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<RealtimeTranslationTranscriptSegment> Segments,
    int Page,
    int PageSize,
    int TotalSegments,
    bool HasActiveSession,
    string SelectedStream,
    int SourceSegmentCount,
    int TranslationSegmentCount);

public sealed record TranscriptTextSegment(int Sequence, string Text, DateTimeOffset CapturedAt);

/// <summary>
/// How a caller wants to enter a transcript. <see cref="AtTime"/> wins, then
/// <see cref="Page"/>, then <see cref="Offset"/>, then the first page. The three exist
/// because they answer different questions: a page is what the reader shows and what the
/// user names ("the first page"); a time is the only thing the source and translation
/// streams share, since their segment counts differ; an offset resumes a page that the
/// character budget cut short.
/// </summary>
public sealed record TranscriptTextPageRequest(
    int? Page = null,
    int? Offset = null,
    DateTimeOffset? AtTime = null,
    string? Stream = null,
    int? Limit = null,
    int MaximumCharacters = 12_000);

/// <summary>
/// A window of transcript text plus everything needed to name it: which page it belongs
/// to, the wall-clock span of that page, and whether the request returned its remainder.
/// <see cref="StartsAt"/> and <see cref="EndsAt"/> describe the whole window that was
/// read, not only the segments that fit, so a truncated page still reports its real end.
/// </summary>
public sealed record TranscriptTextPage(
    Guid Id,
    string Title,
    string TargetLanguage,
    string Stream,
    string SelectedStream,
    IReadOnlyList<TranscriptTextSegment> Segments,
    int Page,
    int PageSize,
    int TotalPages,
    int Offset,
    int TotalSegments,
    bool PageComplete,
    bool HasMore,
    DateTimeOffset? StartsAt,
    DateTimeOffset? EndsAt,
    int SourceSegmentCount,
    int TranslationSegmentCount)
{
    /// <summary>Where to resume when the character budget or explicit limit cut the page short.</summary>
    public int NextOffset => Offset + Segments.Count;
}

public sealed record TranscriptPageSpan(int Page, DateTimeOffset StartsAt, DateTimeOffset EndsAt);

public sealed record CapturedTranslationSegment(
    int Sequence,
    string ProviderEventKey,
    string Text,
    DateTimeOffset CapturedAt,
    string Stream = RealtimeTranslationTranscriptStreams.Source);
