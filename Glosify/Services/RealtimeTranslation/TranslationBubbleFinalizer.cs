namespace Glosify.Services.RealtimeTranslation;

internal sealed class TranslationBubbleFinalizer
{
    internal const int MaximumTranslationCharacters = 800;
    internal const int MaximumBubbleCharacters = 120;
    internal const int MinimumBalancedBubbleCharacters = 40;

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
        var split = SplitSentences(boundedText);
        var committedBubbles = new List<string>();

        if (isFinal)
        {
            foreach (var sentence in split.Completed.Skip(_committedSentenceCount))
            {
                AddBubbles(sentence, committedBubbles);
            }
            AddBubbles(split.Remainder, committedBubbles);
            Reset(null);
            return new TranslationBubbleUpdate(committedBubbles, string.Empty);
        }

        while (_committedSentenceCount < split.Completed.Count
            && _committedSentenceCount < _previousCompletedSentences.Count
            && string.Equals(
                split.Completed[_committedSentenceCount],
                _previousCompletedSentences[_committedSentenceCount],
                StringComparison.Ordinal))
        {
            AddBubbles(split.Completed[_committedSentenceCount], committedBubbles);
            _committedSentenceCount++;
        }

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

    private static SentenceSplit SplitSentences(string text)
    {
        var completed = new List<string>();
        var remainder = text.Trim();
        while (remainder.Length > 0)
        {
            var sentenceEnd = FindCompletedSentenceEnd(remainder);
            if (sentenceEnd <= 0)
            {
                break;
            }
            completed.Add(remainder[..sentenceEnd].Trim());
            remainder = remainder[sentenceEnd..].TrimStart();
        }
        return new SentenceSplit(completed, remainder);
    }

    private static int FindCompletedSentenceEnd(string text)
    {
        for (var index = 0; index < text.Length; index++)
        {
            if (IsWesternSentenceMark(text[index]))
            {
                var end = index + 1;
                while (end < text.Length && IsWesternSentenceMark(text[end]))
                {
                    end++;
                }
                while (end < text.Length && IsSentenceCloser(text[end]))
                {
                    end++;
                }
                if (end == text.Length || char.IsWhiteSpace(text[end]))
                {
                    return end;
                }
            }
            else if (IsCjkSentenceMark(text[index]))
            {
                var end = index + 1;
                while (end < text.Length && IsCjkSentenceMark(text[end]))
                {
                    end++;
                }
                while (end < text.Length && IsSentenceCloser(text[end]))
                {
                    end++;
                }
                return end;
            }
        }
        return -1;
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
        if (RemainingLength(text, wordBoundary) >= MinimumBalancedBubbleCharacters)
        {
            return wordBoundary;
        }

        var ideal = text.Length / 2;
        var beforeIdeal = text.LastIndexOf(' ', Math.Min(ideal, maximumLength));
        var afterIdeal = text.IndexOf(' ', ideal);
        var balanced = new[] { beforeIdeal, afterIdeal }
            .Where(candidate => candidate >= MinimumBalancedBubbleCharacters
                && candidate <= maximumLength
                && RemainingLength(text, candidate) >= MinimumBalancedBubbleCharacters)
            .OrderBy(candidate => Math.Abs(candidate - ideal))
            .FirstOrDefault();
        return balanced > 0 ? balanced : wordBoundary;
    }

    private static int RemainingLength(string text, int splitAt) =>
        text[(splitAt + 1)..].TrimStart().Length;

    private static string KeepLast(string text, int maximumLength) =>
        text.Length <= maximumLength ? text : text[^maximumLength..];

    private static bool IsWesternSentenceMark(char value) => value is '.' or '!' or '?' or '…';

    private static bool IsCjkSentenceMark(char value) => value is '。' or '！' or '？';

    private static bool IsSentenceCloser(char value) => value is '"' or '\'' or '”' or '’' or '»' or ')' or ']' or '）';

    private sealed record SentenceSplit(IReadOnlyList<string> Completed, string Remainder);
}

internal sealed record TranslationBubbleUpdate(
    IReadOnlyList<string> CommittedBubbles,
    string PendingText);
