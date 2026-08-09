using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListSavedTranscriptsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_saved_transcripts",
        "List the user's saved live-subtitle transcripts with title, target language, date, segment count, and id. Use this when the user refers to a saved session without identifying it. Returns up to 50 transcripts per call.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = IntegerProp("Optional number of transcripts to skip. Defaults to 0."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListSavedTranscriptsTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        var offset = GetOffset(args);
        var languageCode = QuizLanguageCatalog.Find(
            context.CurrentLanguageCode ?? context.CurrentLanguage)?.Code;
        var query = _context.RealtimeTranslationTranscripts
            .AsNoTracking()
            .Where(transcript => transcript.UserId == context.UserId
                && languageCode != null
                && transcript.TargetLanguage == languageCode
                && transcript.Segments.Any());
        var total = await query.CountAsync(cancellationToken);
        var transcripts = await query
            .OrderByDescending(transcript => transcript.UpdatedAt)
            .Skip(offset)
            .Take(pageSize)
            .Select(transcript => new
            {
                id = transcript.Id,
                title = transcript.Title,
                target_language = transcript.TargetLanguage,
                learning_language = transcript.TargetLanguage,
                stream = transcript.Stream,
                created_at = transcript.CreatedAt,
                updated_at = transcript.UpdatedAt,
                segment_count = transcript.Segments.Count(segment => segment.Stream == transcript.Stream),
                has_translation_stream = transcript.Segments.Any(segment =>
                    segment.Stream == RealtimeTranslationTranscriptStreams.Translation),
            })
            .ToListAsync(cancellationToken);
        return new
        {
            transcripts,
            total_count = total,
            offset,
            has_more = offset + transcripts.Count < total,
            current_transcript_id = context.TranscriptId,
        };
    }
}
