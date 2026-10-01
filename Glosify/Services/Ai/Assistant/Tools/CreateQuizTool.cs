using System.ComponentModel;
using Glosify.Data;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record CreateQuizArgs(
    [property: Description("Quiz name.")] string Name,
    [property: Description("Translation language: infer from the user's own instructions, excluding pasted study material. English when unclear. Do not ask for confirmation.")] string? SourceLanguage = null,
    [property: Description("Must match the selected learning language from app context. Null uses that language. Never infer it from the request or offer a reversed direction.")] string? TargetLanguage = null,
    [property: Description("Id of the collection to hold the quiz, from list_library. Null for the library root.")] string? CollectionId = null,
    [property: Description("Starter vocabulary. Null for none.")] IReadOnlyList<WordInput>? Words = null,
    [property: Description("Starter full sentences. Null for none.")] IReadOnlyList<SentenceInput>? Sentences = null);

/// <summary>
/// Creates a standard quiz, optionally with its first batch of content, and returns its id so
/// later batches go through add_items.
/// </summary>
internal sealed class CreateQuizTool(GlosifyContext db) : AssistantTool<CreateQuizArgs>
{
    public override string Name => "create_quiz";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;

    protected override string Describe(AssistantMode mode) =>
        $"Create a standard vocabulary quiz, optionally with up to {QuizContent.MaxItemsPerCall} starter words and {QuizContent.MaxItemsPerCall} sentences. Saved immediately and kept private until this request finishes; the user can undo. Returns quiz_id: add the rest of the content with add_items and that quiz_id. Create one quiz per requested quiz, never one per batch.";

    protected override Task<ToolResult> RunAsync(CreateQuizArgs args, ToolContext context, CancellationToken cancellationToken) =>
        CreateAsync(
            db,
            context,
            args.Name,
            args.SourceLanguage ?? "English",
            args.TargetLanguage ?? context.TargetLanguage,
            args.CollectionId,
            args.Words,
            args.Sentences,
            cancellationToken);

    internal static async Task<ToolResult> CreateAsync(
        GlosifyContext db,
        ToolContext context,
        string name,
        string? sourceLanguage,
        string? targetLanguage,
        string? collectionId,
        IReadOnlyList<WordInput>? words,
        IReadOnlyList<SentenceInput>? sentences,
        CancellationToken cancellationToken)
    {
        const string title = "Could not create the quiz";
        if (string.IsNullOrWhiteSpace(name))
        {
            return ToolResult.Fail(title, "name is required.");
        }

        if (context.IsFreestyle)
        {
            sourceLanguage = QuizLanguageCatalog.FreestyleName;
            targetLanguage = QuizLanguageCatalog.FreestyleName;
        }
        else
        {
            if (string.IsNullOrWhiteSpace(context.TargetLanguage))
                return ToolResult.Fail(title, "target_language is required from app context. The user must select a practice language in the app; do not infer it or offer a direction choice.");
            if (!string.IsNullOrWhiteSpace(targetLanguage)
                && !string.Equals(targetLanguage.Trim(), context.TargetLanguage.Trim(), StringComparison.OrdinalIgnoreCase))
                return ToolResult.Fail(title, $"The selected learning language is {context.TargetLanguage}. Keep words and sentences in that language; set target_language to null to use it. Only the translation language is inferred from the user's request.");
            targetLanguage = context.TargetLanguage;
        }

        if (string.IsNullOrWhiteSpace(sourceLanguage)) sourceLanguage = "English";

        Guid? collection = null;
        if (!string.IsNullOrWhiteSpace(collectionId))
        {
            if (!Guid.TryParse(collectionId, out var parsed)
                || !await db.Collections.AnyAsync(
                    candidate => candidate.Id == parsed
                        && candidate.UserId == context.UserId
                        && candidate.Language == targetLanguage.Trim(),
                    cancellationToken))
            {
                return ToolResult.Fail(title, "The collection was not found for this quiz's language. Use list_library to find collection ids.");
            }

            collection = parsed;
        }

        var content = QuizContent.Clean(words, sentences);
        if (QuizContent.WrongContentKind(context, content.Words.Count > 0, content.Sentences.Count > 0) is { } mismatch)
        {
            return ToolResult.Fail(title, mismatch);
        }

        var change = QuizContent.Change(PendingChangeKinds.CreateQuiz, new
        {
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            source_language = sourceLanguage.Trim(),
            target_language = targetLanguage.Trim(),
            collection_id = collection,
            words = content.Words.Select(word => new { word = word.Word, translation = word.Translation }),
            sentences = content.Sentences.Select(sentence => new { text = sentence.Text, translation = sentence.Translation }),
        });
        var starter = content.Words.Count + content.Sentences.Count;
        return ToolResult.Propose(
            starter == 0
                ? $"Create quiz “{name.Trim()}”"
                : $"Create quiz “{name.Trim()}” with {QuizContent.Describe(content.Words.Count, content.Sentences.Count, context.IsFreestyle)}",
            null,
            [change],
            new { skipped = content.Skipped });
    }
}

internal sealed record CreateFreestyleQuizArgs(
    [property: Description("Quiz name.")] string Name,
    [property: Description("Id of the collection to hold the quiz, from list_library. Null for the library root.")] string? CollectionId = null,
    [property: Description("Starter prompt-and-answer items. Null for none.")] IReadOnlyList<ItemInput>? Items = null);

internal sealed class CreateFreestyleQuizTool(GlosifyContext db) : AssistantTool<CreateFreestyleQuizArgs>
{
    public override string Name => "create_quiz";

    public override AssistantToolKind Kind => AssistantToolKind.Write;

    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Freestyle;

    protected override string Describe(AssistantMode mode) =>
        $"Create a prompt-and-answer quiz, optionally with up to {QuizContent.MaxItemsPerCall} starter items. Saved immediately and kept private until this request finishes; the user can undo. Returns quiz_id: add more items with add_items and that quiz_id. Create one quiz per requested quiz, never one per batch.";

    protected override Task<ToolResult> RunAsync(CreateFreestyleQuizArgs args, ToolContext context, CancellationToken cancellationToken) =>
        CreateQuizTool.CreateAsync(
            db,
            context,
            args.Name,
            null,
            null,
            args.CollectionId,
            args.Items?.Select(item => new WordInput(item.Prompt, item.Answer)).ToList(),
            null,
            cancellationToken);
}
