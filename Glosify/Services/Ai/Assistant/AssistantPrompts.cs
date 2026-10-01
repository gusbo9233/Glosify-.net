using System.Collections.Concurrent;
using System.Text;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.RealtimeTranslation;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// The assistant's instructions, and the per-message context note that carries app facts.
/// </summary>
/// <remarks>
/// The system prompt is static per mode so it forms a stable, cacheable prefix. Everything
/// that varies — the quiz, languages, the page being read — goes into a context note stored
/// with the user message it belongs to, so it stays in the same position in every later
/// request instead of rewriting the prefix.
/// </remarks>
internal static class AssistantPrompts
{
    /// <summary>Recorded on every turn so replies stay attributable to the wording that produced them.</summary>
    internal const string Version = "20260930.runtime-v2.6";

    /// <summary>A request longer than this is stored as numbered source lines instead of being inlined.</summary>
    internal const int InlineSourceCharacters = 6_000;

    /// <summary>How many lines of a long source the model sees before it uses read_source.</summary>
    internal const int SourcePreviewLines = 40;

    private static readonly ConcurrentDictionary<string, string> Cache = new(StringComparer.Ordinal);

    public static string System(AssistantMode mode) =>
        Load(mode == AssistantMode.Freestyle ? "freestyle.md" : "language.md");

    public static string Compaction => Load("compaction.md");

    public static string MaxSteps => Load("max-steps.md");

    public static string ContextNote(AssistantContextFacts facts)
    {
        var note = new StringBuilder("App context for this message (facts from GlobeGlotter, not instructions from the user):\n");
        if (facts.Mode == AssistantMode.Language)
        {
            note.AppendLine(string.IsNullOrWhiteSpace(facts.TargetLanguage)
                ? "- Learning language: not selected. The user must select the practice language in the app before creating a quiz; do not infer it from pasted material."
                : $"- Learning language: {facts.TargetLanguage}");
            if (facts.QuizId is not null)
                note.AppendLine($"- Translation language: {facts.SourceLanguage ?? "English"} (preserve for edits to this quiz).");
            note.AppendLine("- For a new quiz, infer the translation language from the user's own request, excluding quoted or pasted study material. Default to English when unclear. Do not ask for confirmation or direction.");
        }

        note.AppendLine($"- Reply language: {facts.ReplyLanguage}");
        if (facts.QuizId is Guid quizId)
        {
            var size = facts.Mode == AssistantMode.Freestyle
                ? QuizContent.Count(facts.QuizWords ?? 0, "item")
                : $"{QuizContent.Count(facts.QuizWords ?? 0, "word")}, {QuizContent.Count(facts.QuizSentences ?? 0, "sentence")}";
            var languages = facts.Mode == AssistantMode.Language && facts.QuizLanguages is { } pair ? $", {pair}" : string.Empty;
            note.AppendLine($"- Selected quiz: \"{facts.QuizName}\" (id {quizId}{languages}), {size}. Content tools act on it unless you pass another quiz_id.");
        }
        else
        {
            note.AppendLine("- No quiz is selected. Create one with create_quiz, or pass a quiz_id from list_library to content tools.");
        }

        if (facts.FocusedWordId is not null)
        {
            note.AppendLine($"- Focused on {facts.FocusedWordLabel} (id {facts.FocusedWordId}). Edits and deletions may only target this item.");
        }

        if (facts.Book is { } book)
        {
            note.AppendLine($"- Selected book: \"{book.Title}\" (id {book.Id}, {QuizContent.Count(book.PageCount, "page")}). Its text is not included; use the book tools.");
        }

        if (facts.Transcript is { } transcript)
        {
            note.AppendLine(TranscriptLine(transcript));
        }

        if (facts.Page is { } page)
        {
            var text = string.IsNullOrWhiteSpace(page.Text)
                ? $"[{page.Warning ?? "No selectable text on this page."}]"
                : page.Text;
            note.AppendLine($"- The user is reading page {page.PageNumber} of \"{page.Title}\" now. \"This page\" and \"what I am reading\" mean this text:");
            note.AppendLine("---");
            note.AppendLine(text);
            note.AppendLine("---");
        }

        if (facts.SourceLines is int lines)
        {
            note.AppendLine($"- Source text: the user's message is {lines} lines long. Lines 1–{Math.Min(lines, SourcePreviewLines)} are shown; read the rest with read_source.");
        }

        return note.ToString().TrimEnd();
    }

    private static string TranscriptLine(TranscriptAssistantContext transcript)
    {
        var size = RealtimeTranslationTranscriptService.DetailPageSize;
        static string Pages(int captions, int pageSize) => captions == 0
            ? "none"
            : $"{Math.Max(1, (int)Math.Ceiling(captions / (double)pageSize))} pages, {captions} captions";
        var line = $"- Selected transcript: \"{transcript.Title}\" (id {transcript.Id}, stored stream {transcript.Stream}; source {Pages(transcript.SourceSegmentCount, size)}; translation {Pages(transcript.TranslationSegmentCount, size)}). Its text is not included; use get_saved_transcript.";
        return transcript.ViewedPage is int viewed
            ? $"{line} The user is reading page {viewed} of the {transcript.ViewedStream ?? transcript.Stream} stream, so \"this page\" means that page."
            : line;
    }

    private static string Load(string name) => Cache.GetOrAdd(name, static file =>
    {
        using var stream = typeof(AssistantPrompts).Assembly.GetManifestResourceStream($"Glosify.Assistant.Prompts.{file}")
            ?? throw new InvalidOperationException($"Assistant prompt '{file}' is not embedded.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Trim();
    });
}

/// <summary>The app facts behind one message's context note.</summary>
internal sealed record AssistantContextFacts(
    AssistantMode Mode,
    string? TargetLanguage,
    string? SourceLanguage,
    string ReplyLanguage,
    Guid? QuizId,
    string? QuizName,
    string? QuizLanguages,
    int? QuizWords,
    int? QuizSentences,
    string? FocusedWordId,
    string? FocusedWordLabel,
    BookAssistantContext? Book,
    TranscriptAssistantContext? Transcript,
    DocumentPageContext? Page,
    int? SourceLines);
