using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.RealtimeTranslation;

public sealed class RealtimeTranslationTranscriptService : IRealtimeTranslationTranscriptService
{
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
        var segments = await _context.RealtimeTranslationTranscriptSegments
            .AsNoTracking()
            .Where(segment => segment.TranscriptId == transcriptId && segment.Stream == selectedStream)
            .OrderBy(segment => segment.CapturedAt)
            .ThenBy(segment => segment.SessionId)
            .ThenBy(segment => segment.Sequence)
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
        int offset,
        int limit,
        int maximumCharacters,
        CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        limit = Math.Clamp(limit, 1, 100);
        maximumCharacters = Math.Clamp(maximumCharacters, 1_000, 12_000);
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
                Total = item.Segments.Count(segment => segment.Stream == item.Stream),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (transcript is null)
        {
            return null;
        }

        var primaryStream = transcript.Stream;
        var rows = await _context.RealtimeTranslationTranscriptSegments
            .AsNoTracking()
            .Where(segment => segment.TranscriptId == transcriptId && segment.Stream == primaryStream)
            .OrderBy(segment => segment.CapturedAt)
            .ThenBy(segment => segment.SessionId)
            .ThenBy(segment => segment.Sequence)
            .Skip(offset)
            .Take(limit)
            .Select(segment => new TranscriptTextSegment(segment.Sequence, segment.Text, segment.CapturedAt))
            .ToListAsync(cancellationToken);

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

        return new TranscriptTextPage(
            transcript.Id,
            transcript.Title,
            transcript.TargetLanguage,
            transcript.Stream,
            bounded,
            offset,
            transcript.Total,
            offset + bounded.Count < transcript.Total);
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
