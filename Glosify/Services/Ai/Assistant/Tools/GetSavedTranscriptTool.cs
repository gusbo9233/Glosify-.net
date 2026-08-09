using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetSavedTranscriptTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_saved_transcript",
        "Read a bounded page of finalized text from one of the user's saved transcripts. New transcripts contain original source speech; legacy transcripts may contain translations. Defaults to the transcript open in the UI. Page through with offset while has_more is true when the user's request needs more text.",
        BuildSchema(new Dictionary<string, object>
        {
            ["transcript_id"] = StringProp("Optional saved transcript id. Omit to use the transcript open in the UI."),
            ["offset"] = IntegerProp("Optional number of caption segments to skip. Defaults to 0."),
            ["limit"] = IntegerProp("Optional maximum segments from 1 to 100. Defaults to 100."),
            ["stream"] = StringProp(
                "Optional caption stream: 'source' for the original speech (the default), or 'translation' for the live translation "
                + "of the same audio, produced by a different model. Request 'translation' only to cross-check a passage where the "
                + "source text looks garbled or ambiguous; it recovers meaning, not the exact target-language wording. Check "
                + "available_streams in the response before asking for a stream."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public GetSavedTranscriptTool(GlosifyContext context) => _context = context;

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
        var offset = GetOffset(args);
        var limit = GetBoundedInt(args, "limit", 100, 1, 100);
        var languageCode = QuizLanguageCatalog.Find(
            context.CurrentLanguageCode ?? context.CurrentLanguage)?.Code;
        var transcript = await _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(item => item.Id == transcriptId
                && item.UserId == context.UserId
                && languageCode != null
                && item.TargetLanguage == languageCode
                && item.Segments.Any())
            .Select(item => new
            {
                item.Id,
                item.Title,
                item.TargetLanguage,
                item.Stream,
                sourceTotal = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Source),
                translationTotal = item.Segments.Count(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
            })
            .SingleOrDefaultAsync(cancellationToken);
        if (transcript is null)
        {
            return new { error = "Saved transcript not found." };
        }

        var requestedStream = GetString(args, "stream");
        var selectedStream = RealtimeTranslationTranscriptService.NormalizeStream(requestedStream)
            ?? transcript.Stream;
        var total = selectedStream == RealtimeTranslationTranscriptStreams.Translation
            ? transcript.translationTotal
            : transcript.sourceTotal;
        if (total == 0)
        {
            return new
            {
                error = $"This transcript has no '{selectedStream}' captions.",
                available_streams = AvailableStreams(transcript.sourceTotal, transcript.translationTotal),
            };
        }

        var rows = await _context.RealtimeTranslationTranscriptSegments
            .AsNoTracking()
            .Where(segment => segment.TranscriptId == transcript.Id && segment.Stream == selectedStream)
            .OrderBy(segment => segment.CapturedAt)
            .ThenBy(segment => segment.SessionId)
            .ThenBy(segment => segment.Sequence)
            .Skip(offset)
            .Take(limit)
            .Select(segment => new { segment.CapturedAt, segment.Text })
            .ToListAsync(cancellationToken);
        const int maximumCharacters = 12_000;
        var captions = new List<object>(rows.Count);
        var characters = 0;
        foreach (var row in rows)
        {
            if (captions.Count > 0 && characters + row.Text.Length > maximumCharacters)
            {
                break;
            }
            var text = row.Text.Length <= maximumCharacters
                ? row.Text
                : row.Text[..maximumCharacters];
            captions.Add(new { captured_at = row.CapturedAt, text });
            characters += text.Length;
        }

        return new
        {
            id = transcript.Id,
            title = transcript.Title,
            target_language = transcript.TargetLanguage,
            learning_language = transcript.TargetLanguage,
            stream = selectedStream,
            available_streams = AvailableStreams(transcript.sourceTotal, transcript.translationTotal),
            captions,
            offset,
            total_segments = total,
            has_more = offset + captions.Count < total,
            next_offset = offset + captions.Count,
        };
    }

    /// <summary>
    /// Mirrors BookDocumentService.GetUserBooksAsync so the agent sees exactly the books
    /// the picker offers. Note the language column holds the display name ("Polish"),
    /// unlike transcripts, which store the code.
    /// </summary>

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
