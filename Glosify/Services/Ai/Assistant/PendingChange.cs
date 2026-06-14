using System.Text.Json;

namespace Glosify.Services;

public sealed record PendingChange(string Kind, JsonElement Payload);

public static class PendingChangeKinds
{
    public const string AddWord = "add_word";
    public const string AddSentence = "add_sentence";
    public const string EditWord = "edit_word";
    public const string DeleteWord = "delete_word";
    public const string RepairSentence = "repair_sentence";
    public const string CreateQuiz = "create_quiz";
    public const string CreateCollection = "create_collection";
}
