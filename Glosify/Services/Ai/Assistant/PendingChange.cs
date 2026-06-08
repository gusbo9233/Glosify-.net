using System.Text.Json;

namespace Glosify.Services;

public sealed record PendingChange(string Kind, JsonElement Payload);

public static class PendingChangeKinds
{
    public const string AddWord = "add_word";
    public const string AddSentence = "add_sentence";
    public const string EditWord = "edit_word";
    public const string DeleteWord = "delete_word";
    public const string SetWordDetail = "set_word_detail";
    public const string RepairSentence = "repair_sentence";
}
