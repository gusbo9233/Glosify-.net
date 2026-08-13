using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class CreateQuizTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "create_vocabulary_quiz",
        "Propose creating a standard vocabulary quiz with words and translations. This tool does not create an interactive custom-quiz document. The change is only saved when the user clicks Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["name"] = StringProp("Quiz name."),
            ["source_language"] = StringProp(
                "Language the user already knows. Defaults to the translation language the "
                + "conversation has established, so it can be omitted rather than asked about."),
            ["target_language"] = StringProp("Language being learned. Defaults to the current app language when available."),
            ["collection_id"] = StringProp("Optional id of the collection that should contain the quiz."),
            ["words"] = new Dictionary<string, object>
            {
                ["type"] = "array",
                ["description"] = "Optional starter vocabulary for the new quiz.",
                ["items"] = new Dictionary<string, object>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object>
                    {
                        ["word"] = StringProp("Word or short phrase in the target language."),
                        ["translation"] = StringProp("Translation in the source language."),
                    },
                    ["required"] = new[] { "word", "translation" },
                },
            },
            ["sentences"] = SentenceArrayProp(
                "Optional standalone example sentences for the new quiz. Full sentences belong "
                + "here, never in words."),
        }, required: ["name"]));

    /// <summary>
    /// The most starter words or sentences one proposal may carry.
    /// </summary>
    /// <remarks>
    /// A whole-chapter extraction can otherwise put hundreds of generated items into one
    /// tool argument, one pending payload and one review card. The overflow is reported as
    /// skipped rather than dropped silently, so the model can propose the rest separately.
    /// </remarks>
    private const int MaxStarterItems = 100;

    public AgentToolDeclaration Declaration => DeclarationValue;

    /// <summary>
    /// The declared name is create_vocabulary_quiz; create_quiz is an undeclared second
    /// name the old dispatcher also accepted. Kept so a saved chat mid-tool-call still
    /// resolves.
    /// </summary>
    public IReadOnlyList<string> Aliases => ["create_quiz"];

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueCreateQuiz(args, context));

    private static object QueueCreateQuiz(JsonElement args, AgentToolContext context)
    {
        var name = GetString(args, "name");
        var sourceLanguage = FirstNonBlank(GetString(args, "source_language"), context.SourceLanguage);
        var targetLanguage = FirstNonBlank(GetString(args, "target_language"), context.CurrentLanguage);
        var collectionId = GetNullableGuidString(args, "collection_id");
        var (words, skippedWords) = Cap(GetWordDrafts(args, "words"));
        var (sentences, skippedSentences) = Cap(GetSentenceDrafts(args, "sentences"));
        JsonElement? customQuiz = null;
        if (args.TryGetProperty("custom_quiz", out var customQuizElement)
            && customQuizElement.ValueKind != JsonValueKind.Null)
        {
            if (customQuizElement.ValueKind != JsonValueKind.Object
                || string.IsNullOrWhiteSpace(GetString(customQuizElement, "name"))
                || !TryGetArray(customQuizElement, "blocks", out var customBlocks)
                || customBlocks.GetArrayLength() == 0)
            {
                return new { error = "custom_quiz requires a name and at least one block." };
            }
            if (words.Count == 0)
            {
                return new { error = "A custom quiz created with a new quiz needs starter words for its bindings." };
            }
            var promptError = ValidateAssistantAnswerPrompts(customBlocks);
            if (promptError != null)
            {
                return InvalidCustomQuizPrompts(promptError);
            }
            customQuiz = customQuizElement.Clone();
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new { error = "name and source_language are required." };
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new { error = "target_language is required when no current app language is selected." };
        }

        if (collectionId.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            source_language = sourceLanguage.Trim(),
            target_language = targetLanguage.Trim(),
            collection_id = collectionId.Value,
            words,
            sentences,
            custom_quiz = customQuiz,
        }, JsonOptions);

        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateQuiz, payload));
        return new
        {
            queued = true,
            kind = PendingChangeKinds.CreateQuiz,
            name = name.Trim(),
            includes_custom_quiz = customQuiz.HasValue,
            word_count = words.Count,
            sentence_count = sentences.Count,
            skipped_words = skippedWords,
            skipped_sentences = skippedSentences,
        };
    }

    private static (IReadOnlyList<T> Kept, IReadOnlyList<SkippedItem> Skipped) Cap<T>(
        (IReadOnlyList<T> Items, IReadOnlyList<SkippedItem> Skipped) parsed)
    {
        if (parsed.Items.Count <= MaxStarterItems)
        {
            return (parsed.Items, parsed.Skipped);
        }

        var skipped = parsed.Skipped.ToList();
        for (var index = MaxStarterItems; index < parsed.Items.Count; index++)
        {
            skipped.Add(new SkippedItem(
                index,
                $"Only the first {MaxStarterItems} items are accepted in one proposal."));
        }

        return (parsed.Items.Take(MaxStarterItems).ToArray(), skipped);
    }
}
