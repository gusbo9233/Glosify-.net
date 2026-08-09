using System.Text.Json;
using Glosify.Models.CustomQuizzes;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class CreateCustomQuizFromContentTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "create_custom_quiz_from_content",
        "Start a new backing vocabulary quiz and an empty custom quiz from source material such as the current book page. This queues only the quiz shells and starter words. After it succeeds, add every custom element with a separate element tool call; word bindings in those calls may use the exact word values supplied here. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["quiz_name"] = StringProp("Name of the backing vocabulary quiz."),
            ["custom_quiz_name"] = StringProp("Name shown for the custom quiz."),
            ["source_language"] = StringProp("Language the user already knows."),
            ["target_language"] = StringProp("Language being learned. Defaults to the current app language."),
            ["collection_id"] = StringProp("Optional collection id for the backing quiz."),
            ["template_id"] = StringProp("Optional id from list_custom_quiz_templates. Sets the visual style for the custom quiz."),
            ["words"] = WordArrayProp("Starter vocabulary needed by the custom quiz."),
        }, required: ["quiz_name", "custom_quiz_name", "source_language", "words"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(QueueCreateCustomQuizFromContent(args, context));

    private static object QueueCreateCustomQuizFromContent(JsonElement args, AgentToolContext context)
    {
        var quizName = GetString(args, "quiz_name")?.Trim();
        var customQuizName = GetString(args, "custom_quiz_name")?.Trim();
        var sourceLanguage = GetString(args, "source_language")?.Trim();
        var targetLanguage = FirstNonBlank(GetString(args, "target_language"), context.CurrentLanguage)?.Trim();
        var collectionId = GetNullableGuidString(args, "collection_id");
        var template = ResolveCustomQuizTemplate(args);
        var (words, skippedWords) = GetWordDrafts(args, "words");
        if (string.IsNullOrWhiteSpace(quizName)
            || string.IsNullOrWhiteSpace(customQuizName)
            || string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return new { error = "quiz_name, custom_quiz_name, and source_language are required." };
        }
        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            return new { error = "target_language is required when no current app language is selected." };
        }
        if (collectionId.Invalid)
        {
            return new { error = "collection_id must be a valid id." };
        }
        if (words.Count == 0)
        {
            return new { error = "At least one starter word is required." };
        }
        if (args.TryGetProperty("blocks", out _))
        {
            return new { error = "create_custom_quiz_from_content queues only the quiz shells and starter words. Call one element tool per element afterward." };
        }

        var draftRef = $"custom-{Guid.NewGuid():N}";
        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.CreateQuiz,
            name = quizName,
            source_language = sourceLanguage,
            target_language = targetLanguage,
            collection_id = collectionId.Value,
            words,
            custom_quiz = new
            {
                name = customQuizName,
                draft_ref = draftRef,
                style_preset = template?.StylePreset ?? CustomQuizStylePresets.Editorial,
            },
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.CreateQuiz, payload));
        context.PendingCustomQuizRef = draftRef;
        context.PendingCustomQuizName = customQuizName;
        return new
        {
            queued = true,
            kind = PendingChangeKinds.CreateQuiz,
            includes_custom_quiz = true,
            name = quizName,
            custom_quiz_name = customQuizName,
            draft_ref = draftRef,
            next = "Add every element now, in this same turn, with one element tool call each. Element calls default to this new custom quiz, so leave custom_quiz_id out.",
            important = "These shells are only half the work. Do not end your turn and do not ask the user to apply anything yet: applying now would produce an empty custom quiz.",
            skipped = skippedWords,
        };
    }
}
