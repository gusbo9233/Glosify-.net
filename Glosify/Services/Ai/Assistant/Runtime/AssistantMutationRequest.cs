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
    internal static bool IsExplicitInitialCommand(string message) => InitialCommand().IsMatch(message);

    [GeneratedRegex(@"^\s*(?:please\s+)?(?:create|generate|build|add|append|insert|include|extend|(?:make|start)\s+(?:a|an|another|one))\b", RegexOptions.IgnoreCase)]
    private static partial Regex InitialCommand();
}
