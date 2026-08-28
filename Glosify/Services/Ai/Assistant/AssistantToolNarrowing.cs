using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Cuts a profile's tool surface down to what the turn's intent allows.
/// </summary>
/// <remarks>
/// Narrowing only. The set returned is always a subset of the profile surface the page context
/// already chose, so an intent misread can cost the model a tool but can never hand it one the
/// page did not permit. Not offering an inappropriate mutation tool is the cheapest way to
/// stop it being called; the handler guards exist for the paths this cannot reach.
/// </remarks>
internal static class AssistantToolNarrowing
{
    private static readonly string[] WordAdditionTools = ["add_word", "add_words", "add_item", "add_items"];
    private static readonly string[] SentenceAdditionTools = ["add_sentence", "add_sentences"];

    public static IReadOnlySet<string> AllowedNames(
        IReadOnlyList<AgentToolDeclaration> profileDeclarations,
        AssistantIntent intent)
    {
        var allowed = new HashSet<string>(
            profileDeclarations.Select(declaration => declaration.Name),
            StringComparer.Ordinal);

        switch (intent.ContentKind)
        {
            case AssistantContentKind.Sentences:
                allowed.ExceptWith(WordAdditionTools);
                break;
            case AssistantContentKind.Words:
                allowed.ExceptWith(SentenceAdditionTools);
                break;
        }

        return allowed;
    }
}
