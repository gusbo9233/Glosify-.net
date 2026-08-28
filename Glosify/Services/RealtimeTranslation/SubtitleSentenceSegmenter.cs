namespace Glosify.Services.RealtimeTranslation;

internal static class SubtitleSentenceSegmenter
{
    public static SubtitleSentenceSplit Split(string text)
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
        return new SubtitleSentenceSplit(completed, remainder);
    }

    public static int CountCompletedSentences(string? text) =>
        string.IsNullOrWhiteSpace(text) ? 0 : Split(text).Completed.Count;

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

    private static bool IsWesternSentenceMark(char value) => value is '.' or '!' or '?' or '…';

    private static bool IsCjkSentenceMark(char value) => value is '。' or '！' or '？';

    private static bool IsSentenceCloser(char value) =>
        value is '"' or '\'' or '”' or '’' or '»' or ')' or ']' or '）';
}

internal sealed record SubtitleSentenceSplit(
    IReadOnlyList<string> Completed,
    string Remainder);
