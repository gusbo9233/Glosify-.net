using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Tools;
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

    // The content intent is enforced where content is filed: a request that names one kind
    // refuses the other kind in add_items and create_quiz, whichever tool the model chose.
    [Theory]
    [InlineData(AssistantContentKind.Sentences, true, false, false)]
    [InlineData(AssistantContentKind.Sentences, false, true, true)]
    [InlineData(AssistantContentKind.Words, false, true, false)]
    [InlineData(AssistantContentKind.Words, true, false, true)]
    [InlineData(AssistantContentKind.Both, true, true, true)]
    [InlineData(AssistantContentKind.Auto, true, true, true)]
    public void Content_intent_refuses_only_the_kind_the_user_did_not_ask_for(
        AssistantContentKind requested,
        bool hasWords,
        bool hasSentences,
        bool allowed)
    {
        var context = new ToolContext { UserId = "user", Mode = AssistantMode.Language, RequestedContentKind = requested };

        Assert.Equal(allowed, QuizContent.WrongContentKind(context, hasWords, hasSentences) is null);
    }
}
