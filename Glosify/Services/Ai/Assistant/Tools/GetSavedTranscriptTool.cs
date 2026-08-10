using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetSavedTranscriptTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_saved_transcript",
        "Read one page of a saved transcript. Pages are the same pages the user sees in the transcript reader, "
        + $"{RealtimeTranslationTranscriptService.DetailPageSize} captions each, so \"the first page\" means page 1 to both of you. "
        + "New transcripts contain original source speech; legacy transcripts may contain translations. Defaults to the "
        + "transcript open in the UI. When page_complete is false the character budget cut the page short: omit page and "
        + "at_time and call again with next_offset to finish that same page. Move on to the next page only once "
        + "page_complete is true and has_more is still true.",
        BuildSchema(new Dictionary<string, object>
        {
            ["transcript_id"] = StringProp("Optional saved transcript id. Omit to use the transcript open in the UI."),
            ["page"] = IntegerProp("Optional 1-based page number, matching the page numbers shown in the reader. Defaults to page 1. A page beyond the end is clamped to the last page."),
            ["at_time"] = StringProp("Optional ISO-8601 timestamp, copied from a captured_at or starts_at value. Returns the page covering that moment. Use this — not page or offset — to look at the same passage in the other stream, because the two streams number their captions separately."),
            ["offset"] = IntegerProp("Optional number of captions to skip. Only needed to resume a page that came back with page_complete false; use next_offset from that response."),
            ["limit"] = IntegerProp($"Optional maximum captions from 1 to {RealtimeTranslationTranscriptService.DetailPageSize}. Defaults to a full page."),
            ["stream"] = StringProp(
                "Optional caption stream: 'source' for the original speech, or 'translation' for the live translation "
                + "of the same audio, produced by a different model. Omitted, it reads the transcript's own stored stream, which is "
                + "'source' for anything recorded recently but 'translation' on legacy transcripts — check the stream field in the "
                + "response before treating captions as original speech. Request 'translation' only to cross-check a passage where the "
                + "source text looks garbled or ambiguous; it recovers meaning, not the exact target-language wording. Check "
                + "available_streams in the response before asking for a stream."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly IRealtimeTranslationTranscriptService _transcripts;

    public GetSavedTranscriptTool(IRealtimeTranslationTranscriptService transcripts) => _transcripts = transcripts;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var idText = FirstNonBlank(GetString(args, "transcript_id"), context.TranscriptId?.ToString());
        if (!Guid.TryParse(idText, out var transcriptId))
        {
            return new { error = "Choose a saved transcript first or provide a valid transcript_id." };
        }
        var languageCode = QuizLanguageCatalog.Find(
            context.CurrentLanguageCode ?? context.CurrentLanguage)?.Code;
        if (languageCode is null)
        {
            return new { error = "Saved transcript not found." };
        }

        var page = await _transcripts.GetTextPageAsync(
            transcriptId,
            context.UserId,
            languageCode,
            new TranscriptTextPageRequest(
                Page: GetOptionalInt(args, "page"),
                Offset: GetOptionalInt(args, "offset"),
                AtTime: GetTimestamp(args, "at_time"),
                Stream: GetString(args, "stream"),
                Limit: GetOptionalInt(args, "limit")),
            cancellationToken);
        if (page is null)
        {
            return new { error = "Saved transcript not found." };
        }
        if (page.TotalSegments == 0)
        {
            return new
            {
                error = $"This transcript has no '{page.SelectedStream}' captions.",
                available_streams = AvailableStreams(page.SourceSegmentCount, page.TranslationSegmentCount),
            };
        }

        return new
        {
            id = page.Id,
            title = page.Title,
            target_language = page.TargetLanguage,
            learning_language = page.TargetLanguage,
            stream = page.SelectedStream,
            available_streams = AvailableStreams(page.SourceSegmentCount, page.TranslationSegmentCount),
            captions = page.Segments
                .Select(segment => new { captured_at = segment.CapturedAt, text = segment.Text })
                .ToList(),
            page_number = page.Page,
            page_size = page.PageSize,
            total_pages = page.TotalPages,
            starts_at = page.StartsAt,
            ends_at = page.EndsAt,
            // False when the character budget cut the page short. The rest of the same
            // page is at next_offset; the page number has not moved on.
            page_complete = page.PageComplete,
            offset = page.Offset,
            total_segments = page.TotalSegments,
            has_more = page.HasMore,
            next_offset = page.NextOffset,
        };
    }

    private static string[] AvailableStreams(int sourceTotal, int translationTotal)
    {
        var streams = new List<string>(2);
        if (sourceTotal > 0)
        {
            streams.Add(RealtimeTranslationTranscriptStreams.Source);
        }
        if (translationTotal > 0)
        {
            streams.Add(RealtimeTranslationTranscriptStreams.Translation);
        }
        return [.. streams];
    }
}
