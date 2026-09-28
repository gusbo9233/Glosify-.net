using System.Text.RegularExpressions;

namespace Glosify.Services.Ai.Assistant;

/// <summary>
/// Reads the product intent a request states outright, so the application rather than the
/// model decides which artifact and which content type the turn is about.
/// </summary>
/// <remarks>
/// Deliberately conservative. Only wording that names the thing counts; anything else stays
/// <see cref="AssistantArtifactKind.Auto"/>/<see cref="AssistantContentKind.Auto"/> and the
/// existing page-based routing decides as before. In particular this never guesses "sentence"
/// from punctuation or length — "by the way" and "as far as I know" are ordinary vocabulary,
/// and a heuristic that treats them as sentences breaks a working feature to fix a rarer one.
/// <para>
/// The term lists are English-only, which matches where the observed failures were reported.
/// They are the place to add localized terms; an unrecognised language degrades to Auto,
/// which is the pre-existing behaviour rather than a wrong answer.
/// </para>
/// </remarks>
internal sealed partial class AssistantIntentResolver
{
    public AssistantIntent Resolve(string? userMessage)
    {
        if (string.IsNullOrWhiteSpace(userMessage))
        {
            return AssistantIntent.Unknown;
        }

        return new AssistantIntent(
            ResolveArtifact(userMessage),
            ResolveContent(userMessage),
            ResolveOperation(userMessage));
    }

    // Creation wins over addition because naming a new artifact describes the turn even when
    // the same sentence also says what to put in it: "create a quiz and add ten words" is one
    // creation, not an addition. The durable completion guard also uses this signal, so
    // explicit prohibitions must not be interpreted as requests to write content.
    private static AssistantOperationKind ResolveOperation(string message)
    {
        // A prohibition covers its coordinated verbs until the sentence ends or a
        // contrast starts a new instruction: "do not create or add; just list".
        message = NegatedArtifacts().Replace(NegatedOperations().Replace(message, " "), " ");
        if (CreateTerms().IsMatch(message))
        {
            return AssistantOperationKind.Create;
        }

        return AddTerms().IsMatch(message)
            ? AssistantOperationKind.Add
            : AssistantOperationKind.Auto;
    }

    private static AssistantArtifactKind ResolveArtifact(string message)
    {
        // An unqualified "quiz" means a standard quiz. A book, transcript or pasted passage is
        // source material for either kind and so implies nothing on its own.
        return StandardArtifactTerms().IsMatch(message)
            ? AssistantArtifactKind.StandardQuiz
            : AssistantArtifactKind.Auto;
    }

    private static AssistantContentKind ResolveContent(string message)
    {
        // "vocabulary quiz" names an artifact, not a request for words, so the artifact
        // phrases come out before the content terms are looked for.
        var withoutArtifactPhrases = ArtifactPhrases().Replace(message, " ");
        var wantsWords = WordTerms().IsMatch(withoutArtifactPhrases);
        var wantsSentences = SentenceTerms().IsMatch(withoutArtifactPhrases);

        return (wantsWords, wantsSentences) switch
        {
            (true, true) => AssistantContentKind.Both,
            (true, false) => AssistantContentKind.Words,
            (false, true) => AssistantContentKind.Sentences,
            _ => AssistantContentKind.Auto,
        };
    }

    [GeneratedRegex(@"\b(quiz|quizzes)\b", RegexOptions.IgnoreCase)]
    private static partial Regex StandardArtifactTerms();

    [GeneratedRegex(
        @"\b(vocabulary|standard|normal|regular|plain) (quiz|quizzes)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex ArtifactPhrases();

    [GeneratedRegex(@"\b(words?|vocab|vocabulary|glossary)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WordTerms();

    [GeneratedRegex(@"\b(sentences?|phrases in context)\b", RegexOptions.IgnoreCase)]
    private static partial Regex SentenceTerms();

    [GeneratedRegex(
        @"\b(?:do\s+not|don['’]t|never|without|not|no\s+need\s+to)\s+(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\b(?:\s*(?:,\s*(?:(?:and|or)\s+)?|(?:and|or)\s+)(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\b)*.*?(?=[.!?;\r\n]|,(?!\s*(?:(?:and|or)\b|(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\s*(?:,|\b(?:and|or)\s+(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\b)))|\b(?:but|instead|then)\b|\b(?:just|please|simply)\s+(?=(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\b)|$)",
        RegexOptions.IgnoreCase)]
    private static partial Regex NegatedOperations();

    [GeneratedRegex(@"\bno\s+(?:new\s+)?(?:quiz|quizzes|collection|list)\b.*?(?=[,.!?;\r\n]|\b(?:but|instead|then)\b|\b(?:just|please|simply)\s+(?=(?:create|generate|build|make|start|add|append|insert|include|extend|edit|move|delete)\b)|$)", RegexOptions.IgnoreCase)]
    private static partial Regex NegatedArtifacts();

    // "start" and "new" only count next to an artifact noun: "start with the dative case" is a
    // lesson request, not a creation.
    [GeneratedRegex(
        @"\b(create|generate|build)\b|\b(make|start)\s+(a|an|another|one)\b|\bnew\s+(quiz|quizzes|collection|list)\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex CreateTerms();

    [GeneratedRegex(@"\b(add|append|insert|include|extend)\b", RegexOptions.IgnoreCase)]
    private static partial Regex AddTerms();
}
