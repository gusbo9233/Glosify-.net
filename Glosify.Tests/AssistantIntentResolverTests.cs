using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The routing decisions that used to be left to the model: which artifact a request means,
/// and which content family it belongs in.
/// </summary>
public sealed class AssistantIntentResolverTests
{
    private readonly AssistantIntentResolver _resolver = new();

    [Theory]
    // The four originally observed failures, in the wording they were reported with.
    [InlineData("Add five Polish sentences with English translations.", AssistantContentKind.Sentences)]
    [InlineData("Add five words.", AssistantContentKind.Words)]
    [InlineData("Add words and sentences from this passage.", AssistantContentKind.Both)]
    [InlineData("Create a Polish quiz with ten words and example sentences.", AssistantContentKind.Both)]
    [InlineData("Help me with the dative case.", AssistantContentKind.Auto)]
    // "vocabulary quiz" names the artifact; it is not a request for words specifically.
    [InlineData("Create a vocabulary quiz about travel.", AssistantContentKind.Auto)]
    public void Content_intent_follows_the_wording(string message, AssistantContentKind expected) =>
        Assert.Equal(expected, _resolver.Resolve(message).ContentKind);

    [Theory]
    [InlineData("Create a Polish travel quiz.", AssistantOperationKind.Create)]
    [InlineData("Generate a quiz from this page.", AssistantOperationKind.Create)]
    [InlineData("Make a new quiz about food.", AssistantOperationKind.Create)]
    [InlineData("Add five words to this quiz.", AssistantOperationKind.Add)]
    [InlineData("Include a few more sentences.", AssistantOperationKind.Add)]
    // Creation names the turn even when the same sentence says what to put in the new artifact.
    [InlineData("Create a quiz and add ten words.", AssistantOperationKind.Create)]
    // Neither verb is about producing content here.
    [InlineData("Why does this take the dative case?", AssistantOperationKind.Auto)]
    [InlineData("Start with the dative case, please.", AssistantOperationKind.Auto)]
    [InlineData("Make sure the translations are right.", AssistantOperationKind.Auto)]
    public void Operation_intent_prefers_creation_over_addition(
        string message,
        AssistantOperationKind expected) =>
        Assert.Equal(expected, _resolver.Resolve(message).OperationKind);

    // Operation is recorded, never enforced: it must not remove a tool the page allowed.
    [Theory]
    [InlineData("Create a quiz with five words.")]
    [InlineData("Add five words.")]
    [InlineData("Why does this take the dative case?")]
    public void Operation_intent_never_narrows_the_tool_surface(string message)
    {
        IReadOnlyList<AgentToolDeclaration> declarations =
        [
            new("add_word", "Adds a word.", new { }),
            new("create_vocabulary_quiz", "Creates a quiz.", new { }),
        ];
        var intent = _resolver.Resolve(message);

        var allowed = AssistantToolNarrowing.AllowedNames(
            declarations,
            intent);
        var withoutOperation = AssistantToolNarrowing.AllowedNames(
            declarations,
            intent with { OperationKind = AssistantOperationKind.Auto });

        Assert.Equal(withoutOperation.OrderBy(name => name), allowed.OrderBy(name => name));
    }

    [Theory]
    [InlineData("Create a normal Polish quiz about travel.", AssistantArtifactKind.StandardQuiz)]
    [InlineData("Create a quiz about travel.", AssistantArtifactKind.StandardQuiz)]
    [InlineData("Create a custom multiple-choice quiz.", AssistantArtifactKind.StandardQuiz)]
    [InlineData("Make an interactive cloze exercise.", AssistantArtifactKind.Auto)]
    [InlineData("Build a fill-in-the-blank drill.", AssistantArtifactKind.Auto)]
    // Source material implies nothing on its own; either kind can be built from a book page.
    [InlineData("Use page 12 of my textbook.", AssistantArtifactKind.Auto)]
    [InlineData("Summarise this transcript for me.", AssistantArtifactKind.Auto)]
    public void Artifact_intent_defaults_an_unqualified_quiz_to_standard(
        string message,
        AssistantArtifactKind expected) =>
        Assert.Equal(expected, _resolver.Resolve(message).ArtifactKind);

    // A multiword expression is ordinary vocabulary. Inferring "sentence" from spaces or
    // length would break adding it, which is why nothing here looks at shape.
    [Fact]
    public void Vocabulary_phrases_are_not_read_as_sentences()
    {
        var intent = _resolver.Resolve("""Add the phrase "by the way" as vocabulary.""");

        Assert.Equal(AssistantContentKind.Words, intent.ContentKind);
    }

    [Fact]
    public void An_empty_message_decides_nothing() =>
        Assert.Equal(AssistantIntent.Unknown, _resolver.Resolve("   "));

    [Fact]
    public void Sentence_intent_withdraws_the_word_addition_tools()
    {
        var allowed = Narrow(AssistantContentKind.Sentences, AssistantArtifactKind.Auto);

        Assert.DoesNotContain("add_word", allowed);
        Assert.DoesNotContain("add_words", allowed);
        Assert.Contains("add_sentence", allowed);
        Assert.Contains("add_sentences", allowed);
        // Editing and deleting need existing ids, so they are unaffected by content intent.
        Assert.Contains("edit_word", allowed);
        Assert.Contains("delete_word", allowed);
    }

    [Fact]
    public void Word_intent_withdraws_the_sentence_addition_tools()
    {
        var allowed = Narrow(AssistantContentKind.Words, AssistantArtifactKind.Auto);

        Assert.DoesNotContain("add_sentence", allowed);
        Assert.DoesNotContain("add_sentences", allowed);
        Assert.Contains("add_word", allowed);
    }

    [Fact]
    public void Standard_quiz_intent_keeps_standard_creation()
    {
        var allowed = Narrow(AssistantContentKind.Auto, AssistantArtifactKind.StandardQuiz);

        Assert.Contains("create_vocabulary_quiz", allowed);
    }

    [Fact]
    public void Narrowing_never_adds_a_tool_the_profile_did_not_offer()
    {
        var declarations = new[] { Declaration("add_word") };

        var allowed = AssistantToolNarrowing.AllowedNames(
            declarations,
            new AssistantIntent(AssistantArtifactKind.Auto, AssistantContentKind.Both));

        Assert.Equal(["add_word"], allowed);
    }

    private static IReadOnlySet<string> Narrow(
        AssistantContentKind content,
        AssistantArtifactKind artifact)
    {
        var declarations = new[]
        {
            "add_word", "add_words", "add_sentence", "add_sentences", "edit_word",
            "delete_word", "create_vocabulary_quiz",
        }.Select(Declaration).ToArray();

        return AssistantToolNarrowing.AllowedNames(
            declarations,
            new AssistantIntent(artifact, content));
    }

    private static AgentToolDeclaration Declaration(string name) =>
        new(name, name, new { type = "object", properties = new { } });
}
