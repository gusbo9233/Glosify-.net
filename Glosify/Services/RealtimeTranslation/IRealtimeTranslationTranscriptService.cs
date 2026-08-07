using Glosify.Models.Entities;

namespace Glosify.Services.RealtimeTranslation;

public interface IRealtimeTranslationTranscriptService
{
    Task<TranscriptLibraryPage> GetLibraryAsync(string userId, string quizLanguageCode, int page, int pageSize, CancellationToken cancellationToken = default);
    Task<TranscriptDetailPage?> GetDetailAsync(Guid transcriptId, string userId, string quizLanguageCode, int page, int pageSize, string? stream = null, CancellationToken cancellationToken = default);
    Task<TranscriptTextPage?> GetTextPageAsync(Guid transcriptId, string userId, string quizLanguageCode, int offset, int limit, int maximumCharacters, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid transcriptId, string userId, string quizLanguageCode, string title, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid transcriptId, string userId, string quizLanguageCode, CancellationToken cancellationToken = default);
    Task AppendAsync(Guid sessionId, IReadOnlyList<CapturedTranslationSegment> segments, CancellationToken cancellationToken = default);
    Task DeleteStaleEmptyAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default);
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

public sealed record TranscriptTextPage(
    Guid Id,
    string Title,
    string TargetLanguage,
    string Stream,
    IReadOnlyList<TranscriptTextSegment> Segments,
    int Offset,
    int TotalSegments,
    bool HasMore);

public sealed record CapturedTranslationSegment(
    int Sequence,
    string ProviderEventKey,
    string Text,
    DateTimeOffset CapturedAt,
    string Stream = RealtimeTranslationTranscriptStreams.Source);
