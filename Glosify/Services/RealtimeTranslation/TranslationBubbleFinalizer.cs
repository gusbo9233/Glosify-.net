namespace Glosify.Services.RealtimeTranslation;

internal sealed class TranslationBubbleFinalizer
{
    internal const int MaximumTranslationCharacters = 800;
    internal const int MaximumBubbleCharacters = 240;
    internal const int MaximumSentencesPerBubble = 2;

    private int? _sequence;
    private IReadOnlyList<string> _previousCompletedSentences = [];
    private int _committedSentenceCount;

    public TranslationBubbleUpdate Apply(int sequence, string text, bool isFinal)
    {
        if (_sequence != sequence)
        {
            Reset(sequence);
        }

        var boundedText = KeepLast(text, MaximumTranslationCharacters).Trim();
        var split = SubtitleSentenceSegmenter.Split(boundedText);
        var committedBubbles = new List<string>();

        if (isFinal)
        {
            AddSentenceBubbles(
                split.Completed.Skip(_committedSentenceCount)
                    .Append(split.Remainder)
                    .Where(value => value.Length > 0),
                committedBubbles);
            Reset(null);
            return new TranslationBubbleUpdate(committedBubbles, string.Empty);
        }

        var commitThrough = _committedSentenceCount;
        while (commitThrough < split.Completed.Count
            && commitThrough < _previousCompletedSentences.Count
            && string.Equals(
                split.Completed[commitThrough],
                _previousCompletedSentences[commitThrough],
                StringComparison.Ordinal))
        {
            commitThrough++;
        }

        // Once another complete sentence follows, the earlier sentence is far enough
        // behind the speech head to publish even if Translator slightly rephrased it.
        commitThrough = Math.Max(commitThrough, split.Completed.Count - 1);
        AddSentenceBubbles(
            split.Completed.Skip(_committedSentenceCount)
                .Take(commitThrough - _committedSentenceCount),
            committedBubbles);
        _committedSentenceCount = commitThrough;
        _previousCompletedSentences = split.Completed;
        var pendingText = string.Join(
            ' ',
            split.Completed.Skip(_committedSentenceCount)
                .Append(split.Remainder)
                .Where(value => value.Length > 0));
        return new TranslationBubbleUpdate(committedBubbles, pendingText);
    }

    private void Reset(int? sequence)
    {
        _sequence = sequence;
        _previousCompletedSentences = [];
        _committedSentenceCount = 0;
    }

    private static void AddSentenceBubbles(
        IEnumerable<string> sentences,
        ICollection<string> destination)
    {
        var group = new List<string>(MaximumSentencesPerBubble);
        var groupLength = 0;
        foreach (var sentence in sentences)
        {
            var trimmed = sentence.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            var combinedLength = groupLength + (group.Count > 0 ? 1 : 0) + trimmed.Length;
            if (group.Count >= MaximumSentencesPerBubble
                || (group.Count > 0 && combinedLength > MaximumBubbleCharacters))
            {
                AddBubbles(string.Join(' ', group), destination);
                group.Clear();
                groupLength = 0;
            }

            if (trimmed.Length > MaximumBubbleCharacters)
            {
                AddBubbles(trimmed, destination);
                continue;
            }

            group.Add(trimmed);
            groupLength += (group.Count > 1 ? 1 : 0) + trimmed.Length;
        }

        AddBubbles(string.Join(' ', group), destination);
    }

    private static void AddBubbles(string text, ICollection<string> destination)
    {
        var remaining = text.Trim();
        while (remaining.Length > 0)
        {
            var splitAt = FindLengthSplit(remaining, MaximumBubbleCharacters);
            var bubble = (splitAt > 0 ? remaining[..splitAt] : remaining).Trim();
            remaining = splitAt > 0 ? remaining[splitAt..].TrimStart() : string.Empty;
            if (bubble.Length > 0)
            {
                destination.Add(bubble);
            }
        }
    }

    private static int FindLengthSplit(string text, int maximumLength)
    {
        if (text.Length <= maximumLength)
        {
            return -1;
        }
        var wordBoundary = text.LastIndexOf(' ', maximumLength);
        if (wordBoundary <= 0)
        {
            return maximumLength;
        }
        return wordBoundary;
    }

    private static string KeepLast(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[^maximumLength..];

}

internal sealed record TranslationBubbleUpdate(
    IReadOnlyList<string> CommittedBubbles,
    string PendingText);
