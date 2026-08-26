using Glosify.Services.RealtimeTranslation;
using Xunit;

namespace Glosify.Tests;

public sealed class TranslationBubbleFinalizerTests
{
    [Fact]
    public void StablePartialSentence_IsCommittedOnlyAfterItRepeats()
    {
        var finalizer = new TranslationBubbleFinalizer();

        var first = finalizer.Apply(7, "First sentence. Second", isFinal: false);
        var second = finalizer.Apply(
            7,
            "First sentence. Second sentence. Third",
            isFinal: false);
        var final = finalizer.Apply(
            7,
            "First sentence. Second sentence. Third sentence.",
            isFinal: true);

        Assert.Empty(first.CommittedBubbles);
        Assert.Equal("First sentence. Second", first.PendingText);
        Assert.Equal(["First sentence."], second.CommittedBubbles);
        Assert.Equal("Second sentence. Third", second.PendingText);
        Assert.Equal(["Second sentence.", "Third sentence."], final.CommittedBubbles);
        Assert.Empty(final.PendingText);
    }

    [Fact]
    public void RevisedSentence_IsNotCommittedUntilTheRevisionStabilizes()
    {
        var finalizer = new TranslationBubbleFinalizer();

        finalizer.Apply(9, "The party is in trouble. More", isFinal: false);
        var revised = finalizer.Apply(
            9,
            "The party is in a tense situation after the vote. More details",
            isFinal: false);
        var stable = finalizer.Apply(
            9,
            "The party is in a tense situation after the vote. More details follow",
            isFinal: false);

        Assert.Empty(revised.CommittedBubbles);
        Assert.Equal(
            ["The party is in a tense situation after the vote."],
            stable.CommittedBubbles);
        Assert.Equal("More details follow", stable.PendingText);
    }

    [Fact]
    public void FinalText_IsSplitIntoBoundedServerBubbles()
    {
        var finalizer = new TranslationBubbleFinalizer();
        var text = string.Join(' ', Enumerable.Repeat("translated", 70)) + ".";

        var result = finalizer.Apply(1, text, isFinal: true);

        Assert.True(result.CommittedBubbles.Count > 1);
        Assert.All(
            result.CommittedBubbles,
            bubble => Assert.InRange(bubble.Length, 1, TranslationBubbleFinalizer.MaximumBubbleCharacters));
        Assert.Equal(text, string.Join(' ', result.CommittedBubbles));
        Assert.Empty(result.PendingText);
    }

    [Fact]
    public void NewSequence_DiscardsPreviousPartialState()
    {
        var finalizer = new TranslationBubbleFinalizer();
        finalizer.Apply(1, "Old sentence. Tail", isFinal: false);

        var next = finalizer.Apply(2, "New sentence. Tail", isFinal: false);

        Assert.Empty(next.CommittedBubbles);
        Assert.Equal("New sentence. Tail", next.PendingText);
    }
}
