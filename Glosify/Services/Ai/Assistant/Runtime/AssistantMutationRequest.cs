using System.Text.RegularExpressions;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>A conservative completion hint, not a natural-language intent classifier.</summary>
internal static partial class AssistantMutationRequest
{
    // OperationKind is a broad analytics label: mentions, questions, and prohibitions
    // can all contain "create" or "new quiz". It must not make a write mandatory.
    // Only a leading affirmative command enables this extra zero-write check. Mixed,
    // indirect, and unrecognised phrasing stays with the model's normal instruction
    // handling. The runtime still checks every attempted mutation, pending draft,
    // approval, and source-coverage requirement independently of this hint.
    internal static bool IsExplicitInitialCommand(string message) =>
        InitialCommand().IsMatch(message) && !message.Contains('?') && !NonAffirmativeRequest().IsMatch(message);

    // Opt out of the extra hint for the entire request; do not infer which clause
    // overrides another or remove any words from the model's instruction.
    [GeneratedRegex(@"\b(?:no|not|never|nothing|zero|don['’]t|without|cancel|stop|instead|rather|forget|wait|actually|only)\b", RegexOptions.IgnoreCase)]
    private static partial Regex NonAffirmativeRequest();

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:create|generate|build|add|append|insert|include|extend|(?:make|start)\s+(?:a|an|another|one|new\s+(?:quiz|quizzes|collection|list)))\b(?!\s+(?:nothing|no|not|zero)\b)", RegexOptions.IgnoreCase)]
    private static partial Regex InitialCommand();
}
