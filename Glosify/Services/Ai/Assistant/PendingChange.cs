using System.Text.Json;

namespace Glosify.Services.Ai.Assistant;

public sealed record PendingChange(string Kind, JsonElement Payload);

public static class PendingChangeKinds
{
    public const string AddWord = "add_word";
    public const string AddSentence = "add_sentence";
    public const string EditWord = "edit_word";
    public const string EditSentence = "edit_sentence";
    public const string DeleteWord = "delete_word";
    public const string DeleteSentence = "delete_sentence";
    public const string CreateQuiz = "create_quiz";
    public const string CreateCollection = "create_collection";
    public const string MoveQuiz = "move_quiz";
    public const string RenameCollection = "rename_collection";
    public const string MoveCollection = "move_collection";
}

/// <summary>
/// Recognizes persisted proposals created before the custom-quiz feature was retired.
/// These names remain only so old saved chats cannot apply a partially supported batch.
/// </summary>
internal static class RetiredPendingChangeKinds
{
    private static readonly HashSet<string> CustomQuizKinds =
    [
        "create_custom_quiz",
        "add_custom_quiz_element",
        "add_custom_quiz_elements",
        "configure_custom_quiz_element",
        "remove_custom_quiz_element",
    ];

    public static bool ContainsCustomQuizChange(PendingChange change)
    {
        if (CustomQuizKinds.Contains(change.Kind))
        {
            return true;
        }

        return change.Kind == PendingChangeKinds.CreateQuiz
            && change.Payload.ValueKind == JsonValueKind.Object
            && change.Payload.TryGetProperty("custom_quiz", out var customQuiz)
            && customQuiz.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined;
    }
}
