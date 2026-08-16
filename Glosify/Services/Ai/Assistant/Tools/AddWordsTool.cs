using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddWordsTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_words",
        "Propose adding multiple words or short phrases to the quiz in one tool call. Prefer this over repeated add_word calls when adding more than one word. Each change is queued; it is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["words"] = WordArrayProp("Words or short phrases to add to the quiz."),
        }, required: ["words"]));

    public AgentToolDeclaration Declaration => DeclarationValue;
    public IReadOnlyList<string> Aliases => ["add_items"];

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueAddWords(args, context));

    private static object QueueAddWords(JsonElement args, AgentToolContext context)
    {
        if (!context.QuizId.HasValue)
        {
            return QuizContextRequired();
        }

        if (WrongContentKind(context, AssistantContentKind.Words) is { } mismatch)
        {
            return mismatch;
        }

        var property = args.TryGetProperty("items", out _) ? "items" : "words";
        var (words, skipped) = GetWordDrafts(args, property);
        if (words.Count == 0)
        {
            return new { error = "At least one valid word and translation is required.", skipped };
        }

        // Anything this turn already proposed as a sentence is not queued again as vocabulary.
        var sentenceKeys = QueuedSentenceKeys(context);
        var sourceIndexes = SourceIndexes(words.Count, skipped);
        var skippedDuplicates = new List<SkippedItem>();
        var queued = 0;
        for (var index = 0; index < words.Count; index++)
        {
            var word = words[index];
            if (sentenceKeys.Contains(NormalizeForDuplicateMatch(word.Word)))
            {
                skippedDuplicates.Add(new SkippedItem(
                    sourceIndexes[index],
                    $"\"{word.Word}\" is already proposed as a sentence. "
                    + "A sentence is stored once, as a sentence."));
                continue;
            }

            var payload = JsonSerializer.SerializeToElement(new
            {
                kind = PendingChangeKinds.AddWord,
                word = word.Word,
                translation = word.Translation,
            }, JsonOptions);
            context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddWord, payload));
            queued++;
        }

        return new
        {
            queued = true,
            kind = "add_words",
            count = queued,
            skipped = (IReadOnlyList<SkippedItem>)[.. skipped, .. skippedDuplicates],
        };
    }
}
