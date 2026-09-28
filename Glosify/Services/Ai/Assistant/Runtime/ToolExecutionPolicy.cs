using System.Text.Json;

namespace Glosify.Services.Ai.Assistant.Runtime;

public sealed record ToolExecutionPolicy(string Operation, string Resource, bool RetrySafe,
    int TimeoutSeconds, IReadOnlyList<string> Prerequisites)
{
    public static ToolExecutionPolicy For(string name) => name switch
    {
        "add_word" or "add_words" or "add_sentence" or "add_sentences" or "add_item" or "add_items" => new("append", "quiz", true, 30, ["owned_quiz"]),
        "edit_word" or "edit_words" or "edit_sentence" or "edit_sentences" or "edit_item" or "edit_items" => new("edit", "quiz", true, 30, ["owned_quiz", "unchanged_target"]),
        "delete_word" or "delete_sentence" or "delete_item" => new("delete", "quiz", true, 30, ["owned_quiz", "approval", "unchanged_target"]),
        "create_quiz" or "create_vocabulary_quiz" or "create_collection" => new("create", "library", true, 30, ["resource_capacity"]),
        "move_quiz" or "move_collection" or "rename_collection" => new("organize", "library", true, 30, ["owned_resource", "approval"]),
        _ when name.StartsWith("get_", StringComparison.Ordinal) || name.StartsWith("list_", StringComparison.Ordinal)
            || name.StartsWith("search_", StringComparison.Ordinal) || name == "read_source_section" => new("read", "context", true, 30, ["owned_resource"]),
        _ => new("control", "task", true, 30, []),
    };
    internal static bool NeedsApproval(IReadOnlyList<PendingChange> changes) =>
        changes.Any(x => x.Kind is PendingChangeKinds.DeleteWord or PendingChangeKinds.DeleteSentence
            or PendingChangeKinds.MoveQuiz or PendingChangeKinds.MoveCollection or PendingChangeKinds.RenameCollection)
        || changes.Count(x => x.Kind is PendingChangeKinds.EditWord or PendingChangeKinds.EditSentence) > 1;
}
public sealed record AssistantToolOutcome(string Status, object? Data = null, string? Error = null, int Saved = 0);
