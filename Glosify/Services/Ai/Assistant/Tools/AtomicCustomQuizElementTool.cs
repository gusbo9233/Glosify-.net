using System.Text.Json;
using System.Text.Json.Nodes;
using Glosify.Data;
using Glosify.Models.CustomQuizzes;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.CustomQuizToolSupport;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// One custom quiz element added per call. The eight concrete tools differ only in
/// the element they declare, so they share this execute path, which reads the element
/// kind back off the tool name.
/// </summary>
internal abstract class AtomicCustomQuizElementTool : IAssistantTool
{
    private readonly GlosifyContext _context;
    private readonly CustomQuizToolStore _customQuizzes;

    protected AtomicCustomQuizElementTool(GlosifyContext context, CustomQuizToolStore customQuizzes)
    {
        _context = context;
        _customQuizzes = customQuizzes;
    }

    public abstract AgentToolDeclaration Declaration { get; }

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        QueueAtomicCustomQuizElementAsync(
            Declaration.Name,
            context.IsFreestyle ? NormalizeFreestyleBindings(args) : args,
            context,
            cancellationToken);

    private static JsonElement NormalizeFreestyleBindings(JsonElement args)
    {
        var root = JsonNode.Parse(args.GetRawText());
        Rewrite(root);
        return JsonSerializer.SerializeToElement(root, JsonOptions);

        static void Rewrite(JsonNode? node)
        {
            if (node is JsonObject value)
            {
                if (value.Remove("item_id", out var itemId)) value["word_id"] = itemId;
                if (value.Remove("item_prompt", out var itemPrompt)) value["word"] = itemPrompt;
                if (value["field"] is JsonValue field && field.TryGetValue<string>(out var fieldName))
                {
                    value["field"] = fieldName switch
                    {
                        "prompt" => "lemma",
                        "answer" => "translation",
                        _ => fieldName,
                    };
                }
                foreach (var child in value.ToArray()) Rewrite(child.Value);
            }
            else if (node is JsonArray array)
            {
                foreach (var child in array) Rewrite(child);
            }
        }
    }

    private async Task<object> QueueAtomicCustomQuizElementAsync(
        string toolName,
        JsonElement args,
        AgentToolContext context,
        CancellationToken ct)
    {
        var target = await _customQuizzes.ResolveCustomQuizTargetAsync(args, context, ct);
        if (target.Error != null)
        {
            return new { error = target.Error };
        }

        JsonElement block;
        if (toolName == "add_custom_quiz_element")
        {
            if (!args.TryGetProperty("element", out var supplied) || supplied.ValueKind != JsonValueKind.Object)
            {
                return new { error = "element is required and must be one custom quiz element." };
            }
            block = supplied.Clone();
        }
        else
        {
            var type = toolName switch
            {
                "add_label" => FirstNonBlank(GetString(args, "label_type"), CustomQuizBlockTypes.InstructionLabel),
                "add_text_input" => FirstNonBlank(GetString(args, "answer_type"), CustomQuizBlockTypes.TextInput),
                "add_checkbox" => CustomQuizBlockTypes.Checkbox,
                "add_choice" => GetString(args, "choice_type"),
                "add_word_bank" => CustomQuizBlockTypes.WordBank,
                "add_submit_button" => CustomQuizBlockTypes.SubmitButton,
                "add_feedback_message" => CustomQuizBlockTypes.FeedbackMessage,
                _ => null,
            };
            if (string.IsNullOrWhiteSpace(type) || !CustomQuizBlockTypes.All.Contains(type))
            {
                return new { error = "A valid element type is required." };
            }

            var properties = new Dictionary<string, object?> { ["type"] = type };
            foreach (var property in args.EnumerateObject())
            {
                if (property.Name is "custom_quiz_id" or "label_type" or "answer_type" or "choice_type") continue;
                properties[property.Name] = property.Value.Clone();
            }
            block = JsonSerializer.SerializeToElement(properties, JsonOptions);
        }

        var blockId = GetString(block, "id")?.Trim();
        var blockType = GetString(block, "type")?.Trim();
        if (string.IsNullOrWhiteSpace(blockId) || string.IsNullOrWhiteSpace(blockType))
        {
            return new { error = "Every element needs a stable id and type." };
        }
        var promptError = ValidateAssistantAnswerPrompts(JsonSerializer.SerializeToElement(new[] { block }, JsonOptions), requireAnswer: false);
        if (promptError != null)
        {
            return InvalidCustomQuizPrompts(promptError);
        }
        var label = GetString(block, "label")?.Trim();
        if (CustomQuizBlockTypes.IsAnswer(blockType)
            && !string.IsNullOrWhiteSpace(label)
            && AnswerLabelAlreadyExists(context, target, label))
        {
            return InvalidCustomQuizPrompts($"The answer question label \"{label}\" is already used in this custom quiz.");
        }

        var payload = JsonSerializer.SerializeToElement(new
        {
            kind = PendingChangeKinds.AddCustomQuizElement,
            custom_quiz_id = target.Id,
            custom_quiz_ref = target.DraftRef,
            custom_quiz_name = target.Name,
            block,
            binding_words_from_draft = target.DraftRef != null,
        }, JsonOptions);
        context.PendingChanges.Add(new PendingChange(PendingChangeKinds.AddCustomQuizElement, payload));
        return new { queued = true, kind = toolName, element_id = blockId, element_type = blockType };
    }
}
