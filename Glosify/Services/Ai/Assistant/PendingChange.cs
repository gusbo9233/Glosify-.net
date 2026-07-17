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
    public const string RepairSentence = "repair_sentence";
    public const string DeleteSentence = "delete_sentence";
    public const string CreateQuiz = "create_quiz";
    public const string CreateCollection = "create_collection";
    public const string MoveQuiz = "move_quiz";
    public const string RenameCollection = "rename_collection";
    public const string MoveCollection = "move_collection";
    public const string CreateCustomQuiz = "create_custom_quiz";
    public const string AddCustomQuizElement = "add_custom_quiz_element";
    public const string AddCustomQuizElements = "add_custom_quiz_elements";
    public const string ConfigureCustomQuizElement = "configure_custom_quiz_element";
    public const string RemoveCustomQuizElement = "remove_custom_quiz_element";
}
