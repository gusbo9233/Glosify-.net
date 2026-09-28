using System.Text.Json;
using Glosify.Services.Ai.Assistant.Tools;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>Completion blockers belong to individual mutation targets, not the last tool call.</summary>
internal static class RuntimeMutationCorrections
{
    internal static string Family(string tool) => tool switch
    {
        "add_words" => "add_word", "add_sentences" => "add_sentence",
        "edit_words" => "edit_word", "edit_sentences" => "edit_sentence", _ => tool,
    };

    // Missing identities can only be repaired by preserving the usable content that
    // survived the rejected call. A model's assertion that two calls are related is
    // not evidence, and an entirely blank item cannot be resolved automatically.
    internal static bool MatchesCorrection(string key, string rejectedTool, string rejectedArguments,
        string correctionTool, string correctionArguments)
    {
        var rejected = ToolArguments.ParseArgs(rejectedArguments);
        var correction = ToolArguments.ParseArgs(correctionArguments);
        static IEnumerable<(string Kind, JsonElement Item)> Items(string tool, JsonElement args)
        {
            var properties = tool switch
            {
                "add_words" => new[] { (args.TryGetProperty("items", out _) ? "items" : "words", "word") },
                "add_sentences" => [("sentences", "sentence")],
                "edit_words" => [("changes", "word")],
                "edit_sentences" => [("changes", "sentence")],
                "create_vocabulary_quiz" => [(args.TryGetProperty("items", out _) ? "items" : "words", "word"), ("sentences", "sentence")],
                _ => Array.Empty<(string, string)>(),
            };
            foreach (var (property, kind) in properties)
                if (ToolArguments.TryGetArray(args, property, out var array))
                    foreach (var item in array.EnumerateArray()) yield return (kind, item);
            if (tool is "add_word" or "edit_word") yield return ("word", args);
            if (tool is "add_sentence" or "edit_sentence") yield return ("sentence", args);
        }
        static string Text(JsonElement item, params string[] names) => item.ValueKind != JsonValueKind.Object ? ""
            : names.Select(name => ToolArguments.GetString(item, name)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))?.Trim() ?? "";
        var original = Items(rejectedTool, rejected).Where(x =>
            key == "tool:" + rejectedTool + ":unidentified:" + RuntimeJson.Hash(x.Item.GetRawText()))
            // Repeated identical input shares one ledger target and needs one correction.
            .DistinctBy(x => (x.Kind, Json: x.Item.GetRawText())).ToArray();
        var candidates = Items(correctionTool, correction).ToArray();
        if (original.Length != 1 || candidates.Length != 1 || original[0].Kind != candidates[0].Kind) return false;
        if (rejectedTool == "create_vocabulary_quiz"
            && Text(rejected, "draft_id") != Text(correction, "draft_id")) return false;
        var oldItem = original[0].Item;
        var newItem = candidates[0].Item;
        var textFields = original[0].Kind == "word" ? new[] { "word", "prompt" } : ["text"];
        var oldText = Text(oldItem, textFields);
        var oldTranslation = Text(oldItem, "translation", "answer");
        return (oldText.Length > 0 || oldTranslation.Length > 0)
            && (oldText.Length == 0 || oldText == Text(newItem, textFields))
            && (oldTranslation.Length == 0 || oldTranslation == Text(newItem, "translation", "answer"));
    }

    internal static void Observe(AssistantRuntimeState state, string tool, string arguments, AssistantToolOutcome outcome, int sequence)
    {
        if (ToolExecutionPolicy.For(tool).Operation is "read" or "control"
            || outcome.Status is not ("correctable" or "partial" or "success" or "proposed")) return;
        var args = ToolArguments.ParseArgs(arguments);
        var result = JsonSerializer.SerializeToElement(outcome.Data, RuntimeJson.Options);
        var accepted = outcome.Status is "success" or "proposed" or "partial";
        var fallback = "tool:" + tool;
        var targets = new List<(string Key, bool Skipped)>();

        string Text(JsonElement item, params string[] names) => item.ValueKind != JsonValueKind.Object ? ""
            : names.Select(name => ToolArguments.GetString(item, name)).FirstOrDefault(x => !string.IsNullOrWhiteSpace(x)) ?? "";
        string Key(string operation, JsonElement item, params string[] names)
        {
            var value = Text(item, names);
            return string.IsNullOrWhiteSpace(value) ? fallback + ":unidentified:" + RuntimeJson.Hash(item.GetRawText())
                : operation + ":" + ToolArguments.NormalizeForDuplicateMatch(value).ToUpperInvariant();
        }
        void Items(string property, string skippedProperty, string operation, params string[] names)
        {
            if (!ToolArguments.TryGetArray(args, property, out var array)) return;
            var skipped = result.ValueKind == JsonValueKind.Object && ToolArguments.TryGetArray(result, skippedProperty, out var errors)
                ? errors.EnumerateArray().Where(x => x.TryGetProperty("index", out var i) && i.TryGetInt32(out _)
                    && ToolArguments.GetString(x, "code") != "already_sentence")
                    .Select(x => x.GetProperty("index").GetInt32()).ToHashSet() : [];
            var index = 0;
            foreach (var item in array.EnumerateArray()) targets.Add((Key(operation, item, names), skipped.Contains(index++)));
        }
        switch (tool)
        {
            case "add_words": Items(args.TryGetProperty("items", out _) ? "items" : "words", "skipped", "add_word", "word", "prompt"); break;
            case "add_sentences": Items("sentences", "skipped", "add_sentence", "text"); break;
            case "edit_words": Items("changes", "skipped", "edit_word", "word_id", "item_id"); break;
            case "edit_sentences": Items("changes", "skipped", "edit_sentence", "sentence_id"); break;
            case "create_vocabulary_quiz":
                var draft = Text(result, "draft_id");
                if (draft.Length == 0 && !accepted) { targets.Add((fallback, false)); break; }
                if (draft.Length == 0) draft = Text(args, "draft_id", "name");
                Items(args.TryGetProperty("items", out _) ? "items" : "words", "skipped_words", "draft:" + draft + ":word", "word", "prompt");
                Items("sentences", "skipped_sentences", "draft:" + draft + ":sentence", "text");
                break;
            default:
                var fields = tool switch
                {
                    "add_word" => new[] { "word", "prompt" },
                    "add_sentence" => ["text"],
                    "edit_word" or "delete_word" => ["word_id", "item_id"],
                    "edit_sentence" or "delete_sentence" => ["sentence_id"],
                    _ => ["quiz_id", "collection_id", "name"],
                };
                targets.Add((Key(tool, args, fields), false));
                break;
        }
        if (targets.Count == 0) targets.Add((fallback, false));
        // Resolve only the successful targets, then retain every skipped/rejected target.
        // A read, an unrelated write, or one correction cannot erase the other failures.
        if (accepted)
        {
            foreach (var target in targets.Where(x => !x.Skipped)) state.UnresolvedMutations.Remove(target.Key);
            if (outcome.Status != "partial") state.UnresolvedMutations.Remove(fallback);
        }
        foreach (var target in targets.Where(x => !accepted || x.Skipped)) state.UnresolvedMutations[target.Key] = sequence;
        state.NeedsCorrection = state.UnresolvedMutations.Count > 0;
    }
}
