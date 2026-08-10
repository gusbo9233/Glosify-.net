using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationTranscriptService : IRealtimeTranslationTranscriptService
{
    /// <summary>
    /// One page of transcript text, shared by the reader and by get_saved_transcript. The
    /// user and the assistant have to mean the same thing by "page 3", so neither surface
    /// gets its own page size.
    /// </summary>
    public const int DetailPageSize = 100;

    /// <summary>
    /// Above this many segments the page index is skipped rather than materialized. Page
    /// time labels are a convenience; a pathological session must not turn them into a
    /// large read.
    /// </summary>
    private const int MaximumIndexedSegments = 20_000;

    private const int MaximumStoredSegmentCharacters = 12_000;
    private readonly GlosifyContext _context;
    private readonly TimeProvider _timeProvider;

    public RealtimeTranslationTranscriptService(GlosifyContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<TranscriptLibraryPage> GetLibraryAsync(
        string userId,
        string quizLanguageCode,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 50);
        var query = _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(transcript => transcript.UserId == userId
                && transcript.TargetLanguage == quizLanguageCode
                && transcript.Segments.Any());
        var total = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(transcript => transcript.UpdatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(transcript => new TranscriptLibraryItem(
                transcript.Id,
                transcript.Title,
                transcript.TargetLanguage,
                transcript.Stream,
                transcript.CreatedAt,
                transcript.UpdatedAt,
                transcript.Segments.Count(segment => segment.Stream == transcript.Stream)))
            .ToListAsync(cancellationToken);
        return new TranscriptLibraryPage(items, page, pageSize, total);
    }

    public async Task<TranscriptDetailPage?> GetDetailAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        int page,
        int pageSize,
        string? stream = null,
        CancellationToken cancellationToken = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, 200);
        var transcript = await _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(item => item.Id == transcriptId
                && item.UserId == userId
                && item.TargetLanguage == quizLanguageCode)
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.TargetLanguage,
                item.Stream,
                item.CreatedAt,
                item.UpdatedAt,
                SourceCount = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Source),
                TranslationCount = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
                HasActiveSession = item.Sessions.Any(session =>
                    session.Status == RealtimeTranslationSessionStatuses.Pending
                    || session.Status == RealtimeTranslationSessionStatuses.Active),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (transcript is null)
        {
            return null;
        }

        var selectedStream = NormalizeStream(stream) ?? transcript.Stream;
        var totalSegments = selectedStream == RealtimeTranslationTranscriptStreams.Translation
            ? transcript.TranslationCount
            : transcript.SourceCount;
        var totalPages = Math.Max(1, (int)Math.Ceiling(totalSegments / (double)pageSize));
        page = Math.Min(page, totalPages);
        var segments = await SegmentsInReadingOrder(transcriptId, selectedStream)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);
        return new TranscriptDetailPage(
            transcript.Id,
            transcript.Title,
            transcript.TargetLanguage,
            transcript.Stream,
            transcript.CreatedAt,
            transcript.UpdatedAt,
            segments,
            page,
            pageSize,
            totalSegments,
            transcript.HasActiveSession,
            selectedStream,
            transcript.SourceCount,
            transcript.TranslationCount);
    }

    public async Task<TranscriptTextPage?> GetTextPageAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        TranscriptTextPageRequest request,
        CancellationToken cancellationToken = default)
    {
        var transcript = await _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(item => item.Id == transcriptId
                && item.UserId == userId
                && item.TargetLanguage == quizLanguageCode
                && item.Segments.Any())
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.TargetLanguage,
                item.Stream,
                SourceCount = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Source),
                TranslationCount = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (transcript is null)
        {
            return null;
        }

        var selectedStream = NormalizeStream(request.Stream) ?? transcript.Stream;
        var total = selectedStream == RealtimeTranslationTranscriptStreams.Translation
            ? transcript.TranslationCount
            : transcript.SourceCount;
        var totalPages = Math.Max(1, (int)Math.Ceiling(total / (double)DetailPageSize));
        var limit = Math.Clamp(request.Limit ?? DetailPageSize, 1, DetailPageSize);
        var maximumCharacters = Math.Clamp(request.MaximumCharacters, 1_000, 12_000);
        var segments = SegmentsInReadingOrder(transcriptId, selectedStream);

        // AtTime wins, then Page, then Offset. A time is the only coordinate the two
        // streams share: their segment counts differ, so the same offset — and the same
        // page number — is a different moment in each.
        int skip;
        if (request.AtTime is DateTimeOffset atTime)
        {
            var before = await _context.RealtimeTranslationTranscriptSegments
                .AsNoTracking()
                .CountAsync(
                    segment => segment.TranscriptId == transcriptId
                        && segment.Stream == selectedStream
                        && segment.CapturedAt < atTime,
                    cancellationToken);
            skip = Math.Min(before, Math.Max(0, total - 1)) / DetailPageSize * DetailPageSize;
        }
        else if (request.Page is int page)
        {
            skip = (Math.Clamp(page, 1, totalPages) - 1) * DetailPageSize;
        }
        else
        {
            skip = Math.Max(0, request.Offset ?? 0);
        }

        // A read never crosses a page boundary, even when it starts mid-page to resume a
        // page the character budget cut short. Reading on into the next page would label
        // the result with the page it started in and a span that runs past that page's end.
        var rows = await segments
            .Skip(skip)
            .Take(Math.Min(limit, DetailPageSize - (skip % DetailPageSize)))
            .Select(segment => new TranscriptTextSegment(segment.Sequence, segment.Text, segment.CapturedAt))
            .ToListAsync(cancellationToken);
        var bounded = BoundByCharacters(rows, maximumCharacters);

        return new TranscriptTextPage(
            transcript.Id,
            transcript.Title,
            transcript.TargetLanguage,
            transcript.Stream,
            selectedStream,
            bounded,
            (skip / DetailPageSize) + 1,
            DetailPageSize,
            totalPages,
            skip,
            total,
            bounded.Count == rows.Count,
            skip + bounded.Count < total,
            rows.Count == 0 ? null : rows[0].CapturedAt,
            rows.Count == 0 ? null : rows[^1].CapturedAt,
            transcript.SourceCount,
            transcript.TranslationCount);
    }

    /// <summary>
    /// The wall-clock span of every page, so the reader can label a jump target with when
    /// it happened. Only the timestamp column is read, and only up to
    /// <see cref="MaximumIndexedSegments"/>; past that the labels are dropped rather than
    /// paid for.
    /// </summary>
    public async Task<IReadOnlyList<TranscriptPageSpan>> GetPageSpansAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        string? stream,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        pageSize = Math.Clamp(pageSize, 1, 200);
        var storedStream = await _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(item => item.Id == transcriptId
                && item.UserId == userId
                && item.TargetLanguage == quizLanguageCode)
            .Select(item => item.Stream)
            .SingleOrDefaultAsync(cancellationToken);
        if (storedStream is null)
        {
            return [];
        }

        var selectedStream = NormalizeStream(stream) ?? storedStream;
        var timestamps = await SegmentsInReadingOrder(transcriptId, selectedStream)
            .Take(MaximumIndexedSegments + 1)
            .Select(segment => segment.CapturedAt)
            .ToListAsync(cancellationToken);
        if (timestamps.Count > MaximumIndexedSegments)
        {
            return [];
        }

        var spans = new List<TranscriptPageSpan>();
        for (var index = 0; index < timestamps.Count; index += pageSize)
        {
            var last = Math.Min(index + pageSize, timestamps.Count) - 1;
            spans.Add(new TranscriptPageSpan((index / pageSize) + 1, timestamps[index], timestamps[last]));
        }
        return spans;
    }

    /// <summary>
    /// The one ordering transcripts are read in. The reader, the page index and the
    /// assistant tool must agree on it or "page 3" stops meaning one thing.
    /// </summary>
    private IQueryable<RealtimeTranslationTranscriptSegment> SegmentsInReadingOrder(
        Guid transcriptId,
        string stream) =>
        _context.RealtimeTranslationTranscriptSegments
            .AsNoTracking()
            .Where(segment => segment.TranscriptId == transcriptId && segment.Stream == stream)
            .OrderBy(segment => segment.CapturedAt)
            .ThenBy(segment => segment.SessionId)
            .ThenBy(segment => segment.Sequence);

    /// <summary>
    /// Stops one read filling a context window, however long the captions turn out to be.
    /// Always yields at least one segment so a caller can never stall on a long caption.
    /// </summary>
    private static List<TranscriptTextSegment> BoundByCharacters(
        IReadOnlyList<TranscriptTextSegment> rows,
        int maximumCharacters)
    {
        var bounded = new List<TranscriptTextSegment>(rows.Count);
        var characters = 0;
        foreach (var row in rows)
        {
            if (bounded.Count > 0 && characters + row.Text.Length > maximumCharacters)
            {
                break;
            }
            var text = row.Text.Length <= maximumCharacters
                ? row.Text
                : row.Text[..maximumCharacters];
            bounded.Add(row with { Text = text });
            characters += text.Length;
        }
        return bounded;
    }

    public async Task RenameAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        string title,
        CancellationToken cancellationToken = default)
    {
        var normalized = title?.Trim() ?? string.Empty;
        if (normalized.Length is < 1 or > 160)
        {
            throw new RealtimeTranslationValidationException("Transcript titles must be between 1 and 160 characters.");
        }
        var transcript = await LoadOwnedAsync(transcriptId, userId, quizLanguageCode, cancellationToken);
        transcript.Title = normalized;
        transcript.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        CancellationToken cancellationToken = default)
    {
        var transcript = await _context.RealtimeTranslationTranscripts
            .Include(item => item.Sessions)
            .SingleOrDefaultAsync(item => item.Id == transcriptId
                && item.UserId == userId
                && item.TargetLanguage == quizLanguageCode, cancellationToken)
            ?? throw new RealtimeTranslationNotFoundException("Saved transcript not found.");
        if (transcript.Sessions.Any(session => session.Status == RealtimeTranslationSessionStatuses.Pending
            || session.Status == RealtimeTranslationSessionStatuses.Active))
        {
            throw new RealtimeTranslationConflictException("Stop live subtitles before deleting this transcript.");
        }

        var assistantThreads = await _context.AssistantThreads
            .Where(thread => thread.UserId == userId && thread.ContextTranscriptId == transcriptId)
            .ToListAsync(cancellationToken);
        foreach (var thread in assistantThreads)
        {
            thread.ContextTranscriptId = null;
        }
        foreach (var session in transcript.Sessions)
        {
            session.TranscriptId = null;
            session.Transcript = null;
        }
        _context.RealtimeTranslationTranscripts.Remove(transcript);
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task AppendAsync(
        Guid sessionId,
        IReadOnlyList<CapturedTranslationSegment> segments,
        CancellationToken cancellationToken = default)
    {
        if (segments.Count == 0)
        {
            return;
        }
        var session = await _context.RealtimeTranslationSessions
            .Include(item => item.Transcript)
            .SingleOrDefaultAsync(item => item.Id == sessionId, cancellationToken);
        if (session?.TranscriptId is not Guid transcriptId
            || session.TranscriptConsentAt is null
            || session.Transcript is null
            || !string.Equals(
                session.Transcript.Stream,
                RealtimeTranslationTranscriptStreams.Source,
                StringComparison.Ordinal))
        {
            return;
        }

        // The source and translation streams number their own segments, so a key is
        // only a duplicate when the stream matches too.
        var keys = segments.Select(segment => segment.ProviderEventKey).Distinct().ToList();
        var stored = await _context.RealtimeTranslationTranscriptSegments
            .Where(segment => segment.SessionId == sessionId && keys.Contains(segment.ProviderEventKey))
            .Select(segment => new { segment.Stream, segment.ProviderEventKey })
            .ToListAsync(cancellationToken);
        var existing = stored
            .Select(segment => (segment.Stream, segment.ProviderEventKey))
            .ToHashSet();
        foreach (var segment in segments)
        {
            var text = segment.Text.Trim();
            if (text.Length == 0 || !existing.Add((segment.Stream, segment.ProviderEventKey)))
            {
                continue;
            }
            if (text.Length > MaximumStoredSegmentCharacters)
            {
                text = text[..MaximumStoredSegmentCharacters];
            }
            _context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
            {
                Id = Guid.NewGuid(),
                TranscriptId = transcriptId,
                SessionId = sessionId,
                Sequence = segment.Sequence,
                Stream = segment.Stream,
                ProviderEventKey = segment.ProviderEventKey,
                Text = text,
                CapturedAt = segment.CapturedAt,
            });
        }
        session.Transcript.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task DeleteStaleEmptyAsync(DateTimeOffset olderThan, CancellationToken cancellationToken = default)
    {
        var stale = await _context.RealtimeTranslationTranscripts
            .Where(transcript => transcript.CreatedAt < olderThan
                && !transcript.Segments.Any()
                && !transcript.Sessions.Any(session =>
                    session.Status == RealtimeTranslationSessionStatuses.Pending
                    || session.Status == RealtimeTranslationSessionStatuses.Active))
            .Include(transcript => transcript.Sessions)
            .ToListAsync(cancellationToken);
        foreach (var transcript in stale)
        {
            foreach (var session in transcript.Sessions)
            {
                session.TranscriptId = null;
            }
        }
        _context.RealtimeTranslationTranscripts.RemoveRange(stale);
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<RealtimeTranslationTranscript> LoadOwnedAsync(
        Guid transcriptId,
        string userId,
        string quizLanguageCode,
        CancellationToken cancellationToken) =>
        await _context.RealtimeTranslationTranscripts.SingleOrDefaultAsync(
            transcript => transcript.Id == transcriptId
                && transcript.UserId == userId
                && transcript.TargetLanguage == quizLanguageCode,
            cancellationToken)
        ?? throw new RealtimeTranslationNotFoundException("Saved transcript not found.");

    internal static string? NormalizeStream(string? stream) => stream?.Trim().ToLowerInvariant() switch
    {
        RealtimeTranslationTranscriptStreams.Source => RealtimeTranslationTranscriptStreams.Source,
        RealtimeTranslationTranscriptStreams.Translation => RealtimeTranslationTranscriptStreams.Translation,
        _ => null,
    };
}
