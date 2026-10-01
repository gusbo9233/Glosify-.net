using System.ComponentModel;
using System.Globalization;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record ListSavedTranscriptsArgs(
    [property: Description("Number of transcripts to skip. Null for 0.")] int? Offset = null);

internal sealed class ListSavedTranscriptsTool(GlosifyContext db) : AssistantTool<ListSavedTranscriptsArgs>
{
    private const int PageSize = 50;

    public override string Name => "list_saved_transcripts";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        $"List the user's saved live-subtitle transcripts with title, language, date, caption count, and id. Use it when the user refers to a saved session without identifying it. Returns up to {PageSize} per call.";

    protected override async Task<ToolResult> RunAsync(ListSavedTranscriptsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var offset = Math.Max(0, args.Offset ?? 0);
        var languageCode = QuizLanguageCatalog.Find(context.TargetLanguageCode ?? context.TargetLanguage)?.Code;
        var query = db.RealtimeTranslationTranscripts.AsNoTracking()
            .Where(transcript => transcript.UserId == context.UserId
                && languageCode != null
                && transcript.TargetLanguage == languageCode
                && transcript.Segments.Any());
        var total = await query.CountAsync(cancellationToken);
        var transcripts = await query
            .OrderByDescending(transcript => transcript.UpdatedAt)
            .Skip(offset)
            .Take(PageSize)
            .Select(transcript => new
            {
                id = transcript.Id,
                title = transcript.Title,
                learning_language = transcript.TargetLanguage,
                stream = transcript.Stream,
                created_at = transcript.CreatedAt,
                updated_at = transcript.UpdatedAt,
                segment_count = transcript.Segments.Count(segment => segment.Stream == transcript.Stream),
                has_translation_stream = transcript.Segments.Any(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
            })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok($"Listed {QuizContent.Count(transcripts.Count, "transcript")}", new
        {
            transcripts,
            total_count = total,
            offset,
            has_more = offset + transcripts.Count < total,
            current_transcript_id = context.TranscriptId,
        });
    }
}

internal enum TranscriptStreamChoice
{
    Source,
    Translation,
}

internal sealed record GetSavedTranscriptArgs(
    [property: Description("Transcript id. Null for the transcript selected for this chat.")] string? TranscriptId = null,
    [property: Description("1-based page number, matching the pages the user sees in the reader. Null for page 1.")] int? Page = null,
    [property: Description("ISO-8601 timestamp copied from captured_at or starts_at. Returns the page covering that moment; use it, not page or offset, to look at the same passage in the other stream.")] string? AtTime = null,
    [property: Description("Captions to skip. Only for resuming a page that came back with page_complete false: pass next_offset.")] int? Offset = null,
    [property: Description("Maximum captions. Null for a full page.")] int? Limit = null,
    [property: Description("'source' for the original speech or 'translation' for the live translation of the same audio. Null for the transcript's stored stream. Request 'translation' only to cross-check a garbled passage: it recovers meaning, not exact wording.")] TranscriptStreamChoice? Stream = null);

internal sealed class GetSavedTranscriptTool(IRealtimeTranslationTranscriptService transcripts) : AssistantTool<GetSavedTranscriptArgs>
{
    public override string Name => "get_saved_transcript";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        $"Read one page of a saved transcript. Pages match the reader, {RealtimeTranslationTranscriptService.DetailPageSize} captions each, so \"the first page\" means page 1 to both of you. New transcripts hold original source speech; legacy ones may hold translations — check stream. When page_complete is false, call again with next_offset to finish the same page; move to the next page only when page_complete is true.";

    protected override async Task<ToolResult> RunAsync(GetSavedTranscriptArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string failure = "Could not read the transcript";
        var id = string.IsNullOrWhiteSpace(args.TranscriptId)
            ? context.TranscriptId
            : Guid.TryParse(args.TranscriptId, out var parsed) ? parsed : null;
        var languageCode = QuizLanguageCatalog.Find(context.TargetLanguageCode ?? context.TargetLanguage)?.Code;
        if (id is null || languageCode is null)
        {
            return ToolResult.Fail(failure, "Saved transcript not found. Choose one first or pass a transcript_id from list_saved_transcripts.");
        }

        DateTimeOffset? atTime = DateTimeOffset.TryParse(
            args.AtTime,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var moment) ? moment : null;
        var page = await transcripts.GetTextPageAsync(
            id.Value,
            context.UserId,
            languageCode,
            new TranscriptTextPageRequest(
                Page: args.Page,
                Offset: args.Offset,
                AtTime: atTime,
                Stream: args.Stream switch
                {
                    TranscriptStreamChoice.Source => RealtimeTranslationTranscriptStreams.Source,
                    TranscriptStreamChoice.Translation => RealtimeTranslationTranscriptStreams.Translation,
                    _ => null,
                },
                Limit: args.Limit),
            cancellationToken);
        if (page is null)
        {
            return ToolResult.Fail(failure, "Saved transcript not found.");
        }

        var streams = AvailableStreams(page.SourceSegmentCount, page.TranslationSegmentCount);
        if (page.TotalSegments == 0)
        {
            return ToolResult.Fail(failure, $"This transcript has no '{page.SelectedStream}' captions.", new { available_streams = streams });
        }

        return ToolResult.Ok($"Read page {page.Page} of “{page.Title}”", new
        {
            id = page.Id,
            title = page.Title,
            learning_language = page.TargetLanguage,
            stream = page.SelectedStream,
            available_streams = streams,
            captions = page.Segments.Select(segment => new { captured_at = segment.CapturedAt, text = segment.Text }).ToList(),
            page_number = page.Page,
            page_size = page.PageSize,
            total_pages = page.TotalPages,
            starts_at = page.StartsAt,
            ends_at = page.EndsAt,
            page_complete = page.PageComplete,
            offset = page.Offset,
            total_segments = page.TotalSegments,
            has_more = page.HasMore,
            next_offset = page.NextOffset,
        });
    }

    private static string[] AvailableStreams(int sourceTotal, int translationTotal) =>
    [
        .. sourceTotal > 0 ? [RealtimeTranslationTranscriptStreams.Source] : Array.Empty<string>(),
        .. translationTotal > 0 ? [RealtimeTranslationTranscriptStreams.Translation] : Array.Empty<string>(),
    ];
}

internal sealed record ListSavedTranslationSessionsArgs(
    [property: Description("Number of sessions to skip. Null for 0.")] int? Offset = null);

internal sealed class ListSavedTranslationSessionsTool(GlosifyContext db) : AssistantTool<ListSavedTranslationSessionsArgs>
{
    private const int PageSize = 50;

    public override string Name => "list_saved_translation_sessions";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) =>
        $"List the user's saved Translator extension sessions for the current learning language, with ids, titles, dates, and translation counts. Returns up to {PageSize} per call.";

    protected override async Task<ToolResult> RunAsync(ListSavedTranslationSessionsArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var offset = Math.Max(0, args.Offset ?? 0);
        var languageCode = QuizLanguageCatalog.Find(context.TargetLanguageCode ?? context.TargetLanguage)?.Code;
        var query = db.SavedTranslationSessions.AsNoTracking()
            .Where(session => session.UserId == context.UserId
                && languageCode != null
                && session.LanguageCode == languageCode
                && session.Translations.Any());
        var total = await query.CountAsync(cancellationToken);
        var sessions = await query
            .OrderByDescending(session => session.UpdatedAt)
            .ThenByDescending(session => session.Id)
            .Skip(offset)
            .Take(PageSize)
            .Select(session => new
            {
                id = session.Id,
                title = session.Title,
                learning_language = session.LanguageCode,
                created_at = session.CreatedAt,
                updated_at = session.UpdatedAt,
                translation_count = session.Translations.Count,
            })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok($"Listed {QuizContent.Count(sessions.Count, "translation session")}", new
        {
            sessions,
            total_count = total,
            offset,
            has_more = offset + sessions.Count < total,
        });
    }
}

internal sealed record GetSavedTranslationSessionArgs(
    [property: Description("Session id from list_saved_translation_sessions.")] string SessionId,
    [property: Description("Translations to skip. Null for 0; use next_offset to continue.")] int? Offset = null,
    [property: Description("Maximum translations, 1 to 20. Null for 10.")] int? Limit = null);

internal sealed class GetSavedTranslationSessionTool(GlosifyContext db) : AssistantTool<GetSavedTranslationSessionArgs>
{
    private const int MaximumEntries = 20;
    private const int MaximumCharacters = 12_000;

    public override string Name => "get_saved_translation_session";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) =>
        "Read saved source-and-translation pairs from one Translator extension session, in chronological order. When has_more is true, call again with next_offset.";

    protected override async Task<ToolResult> RunAsync(GetSavedTranslationSessionArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string failure = "Could not read the translation session";
        if (!Guid.TryParse(args.SessionId, out var sessionId))
        {
            return ToolResult.Fail(failure, "Provide a session_id from list_saved_translation_sessions.");
        }

        var languageCode = QuizLanguageCatalog.Find(context.TargetLanguageCode ?? context.TargetLanguage)?.Code;
        var session = await db.SavedTranslationSessions.AsNoTracking()
            .Where(item => item.Id == sessionId
                && item.UserId == context.UserId
                && languageCode != null
                && item.LanguageCode == languageCode)
            .Select(item => new { item.Id, item.LanguageCode, item.Title, Count = item.Translations.Count })
            .SingleOrDefaultAsync(cancellationToken);
        if (session is null)
        {
            return ToolResult.Fail(failure, "Saved translation session not found.");
        }

        var offset = Math.Max(0, args.Offset ?? 0);
        if (session.Count > 0 && offset >= session.Count)
        {
            offset = session.Count - 1;
        }

        var candidates = await db.SavedTranslations.AsNoTracking()
            .Where(item => item.SessionId == session.Id && item.UserId == context.UserId)
            .OrderBy(item => item.CreatedAt)
            .ThenBy(item => item.Id)
            .Skip(offset)
            .Take(Math.Clamp(args.Limit ?? 10, 1, MaximumEntries))
            .Select(item => new
            {
                id = item.Id,
                source_language = item.SourceLanguage,
                detected_source_language = item.DetectedSourceLanguage,
                target_language = item.TargetLanguage,
                preferences = item.Preferences,
                source_text = item.SourceText,
                translated_text = item.TranslatedText,
                saved_at = item.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        var translations = new List<object>(candidates.Count);
        var characters = 0;
        foreach (var candidate in candidates)
        {
            var size = candidate.source_text.Length + candidate.translated_text.Length + (candidate.preferences?.Length ?? 0);
            if (translations.Count > 0 && characters + size > MaximumCharacters)
            {
                break;
            }

            translations.Add(candidate);
            characters += size;
        }

        var nextOffset = offset + translations.Count;
        return ToolResult.Ok($"Read {QuizContent.Count(translations.Count, "saved translation")} from “{session.Title}”", new
        {
            id = session.Id,
            learning_language = session.LanguageCode,
            title = session.Title,
            translations,
            offset,
            total_translations = session.Count,
            has_more = nextOffset < session.Count,
            next_offset = nextOffset,
        });
    }
}
