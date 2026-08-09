using System.Text.Json;
using Glosify.Services.CustomQuizzes;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// The checks the custom quiz tools share: resolving which document a call means, and
/// refusing element definitions that would produce an unanswerable drill.
/// </summary>
/// <remarks>
/// Moved verbatim out of the 2,991-line AssistantTools.
/// </remarks>
internal static class CustomQuizToolSupport
{
    internal static bool AnswerLabelAlreadyExists(AgentToolContext context, CustomQuizTarget target, string label)
    {
        if (!string.IsNullOrWhiteSpace(target.DefinitionJson))
        {
            try
            {
                using var document = JsonDocument.Parse(target.DefinitionJson);
                if (document.RootElement.TryGetProperty("blocks", out var existing)
                    && existing.ValueKind == JsonValueKind.Array
                    && existing.EnumerateArray().Any(block =>
                        CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty)
                        && string.Equals(GetString(block, "label")?.Trim(), label, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
            catch (JsonException)
            {
                // Stored-document validation is handled by the custom quiz service.
            }
        }

        return context.PendingChanges
            .Where(change => change.Kind == PendingChangeKinds.AddCustomQuizElement)
            .Where(change => TargetMatches(change.Payload, target))
            .Select(change => change.Payload.TryGetProperty("block", out var block) ? block : default)
            .Any(block => block.ValueKind == JsonValueKind.Object
                && CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty)
                && string.Equals(GetString(block, "label")?.Trim(), label, StringComparison.OrdinalIgnoreCase));
    }

    internal static bool TargetMatches(JsonElement payload, CustomQuizTarget target)
    {
        if (target.Id.HasValue)
        {
            return Guid.TryParse(GetString(payload, "custom_quiz_id"), out var id) && id == target.Id.Value;
        }
        return string.Equals(GetString(payload, "custom_quiz_ref"), target.DraftRef, StringComparison.Ordinal);
    }

    internal static (Guid? Id, string? Error) ResolveCustomQuizId(JsonElement args, AgentToolContext context)
    {
        var supplied = GetString(args, "custom_quiz_id");
        if (string.IsNullOrWhiteSpace(supplied))
        {
            return context.CustomQuizId.HasValue
                ? (context.CustomQuizId, null)
                : (null, "Choose or open a custom quiz first.");
        }
        return Guid.TryParse(supplied, out var parsed)
            ? (parsed, null)
            : (null, "custom_quiz_id must be a valid id.");
    }

    internal static bool CustomQuizDocumentContainsBlock(string definitionJson, string blockId)
    {
        try
        {
            using var document = JsonDocument.Parse(definitionJson);
            return document.RootElement.TryGetProperty("blocks", out var blocks)
                && blocks.ValueKind == JsonValueKind.Array
                && blocks.EnumerateArray().Any(block => string.Equals(GetString(block, "id"), blockId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    internal static object InvalidCustomQuizPrompts(string error) => new
    {
        error,
        invalid_custom_quiz_questions = true,
        correction = "Give every answer control a specific learner-visible label containing that question's prompt or gap. For text inputs, put {{blank}} where the compact real input belongs and never draw a second blank with underscores or dots. Use different labels for different answers, then call the custom quiz tool again.",
    };

    internal static string? ValidateAssistantAnswerPrompts(JsonElement blocks, bool requireAnswer = true)
    {
        var answers = blocks.EnumerateArray()
            .Where(block => block.ValueKind == JsonValueKind.Object
                && CustomQuizBlockTypes.IsAnswer(GetString(block, "type") ?? string.Empty))
            .Select(block => new
            {
                Id = FirstNonBlank(GetString(block, "id"), "unnamed"),
                Type = GetString(block, "type") ?? string.Empty,
                Label = FirstNonBlank(GetString(block, "label"), GetString(block, "text"))?.Trim(),
            })
            .ToList();

        if (requireAnswer && answers.Count == 0)
        {
            return "A custom quiz must contain at least one answer control with its own visible question label.";
        }

        var missing = answers.Where(answer => string.IsNullOrWhiteSpace(answer.Label)).Select(answer => answer.Id).ToList();
        if (missing.Count > 0)
        {
            return $"Answer elements are missing learner-visible question labels: {string.Join(", ", missing)}.";
        }

        var drawnBlanks = answers
            .Where(answer => answer.Type == CustomQuizBlockTypes.TextInput
                && System.Text.RegularExpressions.Regex.IsMatch(answer.Label!, @"_{2,}|\.{3,}"))
            .Select(answer => answer.Id)
            .ToList();
        if (drawnBlanks.Count > 0)
        {
            return $"Text input labels must use {{{{blank}}}} for the real inline control instead of underscores or dots: {string.Join(", ", drawnBlanks)}.";
        }

        var duplicates = answers
            .GroupBy(answer => answer.Label!, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => $"\"{group.Key}\"")
            .ToList();
        return duplicates.Count == 0
            ? null
            : $"Answer question labels must be distinct. Repeated: {string.Join(", ", duplicates)}.";
    }

    internal static object DescribeQueuedCustomQuiz(AgentToolContext context)
    {
        var blocks = context.PendingChanges
            .Where(change => change.Kind == PendingChangeKinds.AddCustomQuizElement)
            .Where(change => string.Equals(
                GetString(change.Payload, "custom_quiz_ref"),
                context.PendingCustomQuizRef,
                StringComparison.Ordinal))
            .Select(change => change.Payload.TryGetProperty("block", out var block) ? block : default)
            .Where(block => block.ValueKind == JsonValueKind.Object)
            .ToList();

        var types = blocks.Select(block => GetString(block, "type")).ToList();
        var missing = new List<string>();
        if (types.Count(type => type == CustomQuizBlockTypes.SubmitButton) != 1)
        {
            missing.Add("Add exactly one submit button.");
        }
        if (types.Count(type => type == CustomQuizBlockTypes.FeedbackMessage) != 1)
        {
            missing.Add("Add exactly one feedback message.");
        }
        if (!types.Any(type => type is not null && CustomQuizBlockTypes.IsAnswer(type)))
        {
            missing.Add("Add at least one answer control.");
        }

        return new
        {
            queued = true,
            name = context.PendingCustomQuizName,
            draft_ref = context.PendingCustomQuizRef,
            element_count = blocks.Count,
            elements = blocks.Select(block => new
            {
                id = GetString(block, "id"),
                type = GetString(block, "type"),
                label = GetString(block, "label") ?? GetString(block, "text"),
            }),
            validation_errors = missing,
            note = "This custom quiz is queued in this turn and has no stored document yet. Keep adding its elements now; do not wait for the user to apply it.",
        };
    }

    internal static CustomQuizTemplateSummary? ResolveCustomQuizTemplate(JsonElement args)
    {
        var templateId = GetString(args, "template_id");
        return string.IsNullOrWhiteSpace(templateId)
            ? null
            : new CustomQuizTemplateCatalog().List().FirstOrDefault(template => template.Id == templateId);
    }
}
