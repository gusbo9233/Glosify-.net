using System.Text.RegularExpressions;

namespace Glosify.Services;

public static class VocabularyInputCleaner
{
    private static readonly string[] PronunciationMarkers =
    [
        "pronunciation",
        "pronounciation",
        "pronounced",
        "pronounce",
        "sounds like",
        "say it like",
        "phonetic",
        "ipa"
    ];

    private static readonly Regex LeadingAnnotationPattern = new(
        @"(?<![\p{L}\p{M}])(?:m/f|f/m|m\.?/f\.?|f\.?/m\.?|masc(?:uline)?|fem(?:inine)?|neut(?:er)?|sg|pl|singular|plural|pol|eng|en|english|polish)(?![\p{L}\p{M}])\.?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CleanForVocabulary(string input)
    {
        var lines = Regex.Split(input, @"\r?\n")
            .Select(CleanLine)
            .Where(line => !string.IsNullOrWhiteSpace(line));

        return string.Join("\n", lines);
    }

    public static IReadOnlyList<string> CleanSourceSentences(IReadOnlyList<string> sourceSentences)
    {
        return sourceSentences
            .Select(CleanLine)
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static string CleanLine(string line)
    {
        var markerIndex = FindFirstPronunciationMarker(line);
        if (markerIndex >= 0)
        {
            line = line[..markerIndex];
        }

        line = LeadingAnnotationPattern.Replace(line, " ");
        line = Regex.Replace(line, @"[()\[\]{}]", " ");
        line = Regex.Replace(line, @"[/\\|]+", " ");
        line = Regex.Replace(line, @"\s+", " ").Trim();
        line = Regex.Replace(line, @"\s+([?!.,;:])", "$1");

        return line;
    }

    private static int FindFirstPronunciationMarker(string line)
    {
        var firstIndex = -1;
        foreach (var marker in PronunciationMarkers)
        {
            var match = Regex.Match(line, $@"(?<![\p{{L}}\p{{M}}]){Regex.Escape(marker)}(?![\p{{L}}\p{{M}}])", RegexOptions.IgnoreCase);
            if (match.Success && (firstIndex < 0 || match.Index < firstIndex))
            {
                firstIndex = match.Index;
            }
        }

        return firstIndex;
    }
}
