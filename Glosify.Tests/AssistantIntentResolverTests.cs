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
    [InlineData("Create a normal Polish quiz about travel.", AssistantArtifactKind.StandardQuiz)]
    [InlineData("Create a quiz about travel.", AssistantArtifactKind.StandardQuiz)]
    [InlineData("Create a custom multiple-choice quiz.", AssistantArtifactKind.CustomQuiz)]
    [InlineData("Make an interactive cloze exercise.", AssistantArtifactKind.CustomQuiz)]
    [InlineData("Build a fill-in-the-blank drill.", AssistantArtifactKind.CustomQuiz)]
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
    public void Standard_quiz_intent_withdraws_custom_creation()
    {
        var allowed = Narrow(AssistantContentKind.Auto, AssistantArtifactKind.StandardQuiz);

        Assert.DoesNotContain("create_custom_quiz", allowed);
        Assert.DoesNotContain("add_choice", allowed);
        Assert.Contains("create_vocabulary_quiz", allowed);
    }

    // With the creator open, the element tools are the session. Wording that mentions a quiz
    // must not strip the editor the user is looking at.
    [Fact]
    public void The_open_custom_quiz_builder_keeps_its_element_tools()
    {
        var declarations = new[]
        {
            Declaration("add_choice"),
            Declaration("configure_custom_quiz_element"),
        };

        var allowed = AssistantToolNarrowing.AllowedNames(
            declarations,
            new AssistantIntent(AssistantArtifactKind.StandardQuiz, AssistantContentKind.Auto),
            AssistantAgentProfile.CustomQuizBuilder);

        Assert.Contains("add_choice", allowed);
        Assert.Contains("configure_custom_quiz_element", allowed);
    }

    [Fact]
    public void Narrowing_never_adds_a_tool_the_profile_did_not_offer()
    {
        var declarations = new[] { Declaration("add_word") };

        var allowed = AssistantToolNarrowing.AllowedNames(
            declarations,
            new AssistantIntent(AssistantArtifactKind.CustomQuiz, AssistantContentKind.Both),
            AssistantAgentProfile.QuizAssistant);

        Assert.Equal(["add_word"], allowed);
    }

    private static IReadOnlySet<string> Narrow(
        AssistantContentKind content,
        AssistantArtifactKind artifact)
    {
        var declarations = new[]
        {
            "add_word", "add_words", "add_sentence", "add_sentences", "edit_word",
            "delete_word", "create_vocabulary_quiz", "create_custom_quiz", "add_choice",
        }.Select(Declaration).ToArray();

        return AssistantToolNarrowing.AllowedNames(
            declarations,
            new AssistantIntent(artifact, content),
            AssistantAgentProfile.QuizAssistant);
    }

    private static AgentToolDeclaration Declaration(string name) =>
        new(name, name, new { type = "object", properties = new { } });
}
