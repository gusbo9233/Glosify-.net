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

    private static readonly HashSet<string> PronunciationTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "an",
        "chi",
        "gyel",
        "khe",
        "ktosh",
        "mav",
        "mi",
        "moo",
        "myem",
        "mye",
        "mnye",
        "nye",
        "pa",
        "nee",
        "pol",
        "pro",
        "ro",
        "roz",
        "she",
        "skoo",
        "tak",
        "tro",
        "tso",
        "vee",
        "veesh",
        "vyem",
        "yai",
        "zna",
        "zoo"
    };

    private static readonly Regex StandaloneNoisePattern = new(
        @"^\s*(?:\d+|language difficulties)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LeadingAnnotationPattern = new(
        @"(?<![\p{L}\p{M}])(?:m/f|f/m|m\.?/f\.?|f\.?/m\.?|masc(?:uline)?|fem(?:inine)?|neut(?:er)?|sg|pl|singular|plural|pol|eng|en)(?![\p{L}\p{M}])\.?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string CleanForVocabulary(string input)
    {
        var output = new List<string>();
        var pendingContinuations = new List<string>();

        foreach (var rawLine in Regex.Split(input, @"\r?\n"))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                FlushPendingContinuations(output, pendingContinuations);
                continue;
            }

            AddMergedLines(output, pendingContinuations, CleanLine(rawLine));
        }

        FlushPendingContinuations(output, pendingContinuations);
        return string.Join("\n", output.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    public static IReadOnlyList<string> CleanSourceSentences(IReadOnlyList<string> sourceSentences)
    {
        var output = new List<string>();
        var pendingContinuations = new List<string>();
        foreach (var sourceSentence in sourceSentences)
        {
            AddMergedLines(output, pendingContinuations, CleanLine(sourceSentence));
        }

        FlushPendingContinuations(output, pendingContinuations);
        return output;
    }

    private static IReadOnlyList<string> CleanLine(string line)
    {
        if (StandaloneNoisePattern.IsMatch(line))
        {
            return [];
        }

        var markerIndex = FindFirstPronunciationMarker(line);
        if (markerIndex >= 0)
        {
            line = line[..markerIndex];
        }

        line = StripInlinePronunciation(line);
        line = StripTrailingSourcePrompt(line);
        if (LooksLikeEnglishPrompt(line))
        {
            return [];
        }

        return ExpandSlashAlternatives(line)
            .Select(CleanExpandedLine)
            .Where(cleaned => !string.IsNullOrWhiteSpace(cleaned))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string CleanExpandedLine(string line)
    {
        line = LeadingAnnotationPattern.Replace(line, " ");
        line = Regex.Replace(line, @"[()\[\]{}]", " ");
        line = Regex.Replace(line, @"[/\\|]+", " ");
        line = Regex.Replace(line, @"\s+", " ").Trim();
        line = Regex.Replace(line, @"\s+([?!.,;:])", "$1");

        return line;
    }

    private static void AddMergedLines(
        List<string> output,
        List<string> pendingContinuations,
        IReadOnlyList<string> lines)
    {
        var cleanLines = lines
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (cleanLines.Count == 0)
        {
            return;
        }

        if (pendingContinuations.Count == 0)
        {
            if (EndsSentenceGroup(cleanLines))
            {
                output.AddRange(cleanLines);
            }
            else
            {
                pendingContinuations.AddRange(cleanLines);
            }

            return;
        }

        var merged = pendingContinuations
            .SelectMany(pending => cleanLines.Select(line => $"{pending} {line}".Trim()))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        pendingContinuations.Clear();

        if (EndsSentenceGroup(cleanLines))
        {
            output.AddRange(merged);
        }
        else
        {
            pendingContinuations.AddRange(merged);
        }
    }

    private static void FlushPendingContinuations(List<string> output, List<string> pendingContinuations)
    {
        output.AddRange(pendingContinuations);
        pendingContinuations.Clear();
    }

    private static bool EndsSentence(string line)
    {
        return Regex.IsMatch(line.Trim(), @"[.!?]$");
    }

    private static bool EndsSentenceGroup(IReadOnlyList<string> lines)
    {
        return lines.All(EndsSentence);
    }

    private static IReadOnlyList<string> ExpandSlashAlternatives(string line)
    {
        var alternatives = new List<string> { line };
        foreach (Match match in Regex.Matches(line, @"(?<![\p{L}\p{M}])(?<left>[\p{L}\p{M}]+)/(?<right>[\p{L}\p{M}]+)(?![\p{L}\p{M}])"))
        {
            var token = match.Value;
            if (new[] { "m/f", "f/m" }.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            var left = match.Groups["left"].Value;
            var right = match.Groups["right"].Value;
            alternatives = alternatives
                .SelectMany(item => new[]
                {
                    ReplaceFirst(item, token, left),
                    ReplaceFirst(item, token, right)
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(8)
                .ToList();
        }

        return alternatives;
    }

    private static string ReplaceFirst(string text, string oldValue, string newValue)
    {
        var index = text.IndexOf(oldValue, StringComparison.Ordinal);
        return index < 0
            ? text
            : string.Concat(text.AsSpan(0, index), newValue, text.AsSpan(index + oldValue.Length));
    }

    private static string StripTrailingSourcePrompt(string line)
    {
        var match = Regex.Match(line, @"^(?<target>.+?[?!.,;:])\s+(?<tail>.+)$");
        if (!match.Success)
        {
            return line;
        }

        var target = match.Groups["target"].Value;
        var tail = match.Groups["tail"].Value;
        if (!HasTargetPhraseSignal(Regex.Matches(target, @"\S+").Cast<Match>().Select(item => item.Value))
            || !LooksLikeEnglishPrompt(tail))
        {
            return line;
        }

        return target;
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

    private static string StripInlinePronunciation(string line)
    {
        var matches = Regex.Matches(line, @"\S+");
        if (matches.Count < 2)
        {
            return line;
        }

        for (var splitIndex = 1; splitIndex < matches.Count; splitIndex++)
        {
            var prefixTokens = matches.Cast<Match>().Take(splitIndex).Select(match => match.Value).ToList();
            var tailTokens = matches.Cast<Match>().Skip(splitIndex).Select(match => match.Value).ToList();
            if (!HasTargetPhraseSignal(prefixTokens) || !LooksLikePronunciationTail(tailTokens))
            {
                continue;
            }

            return line[..matches[splitIndex].Index].TrimEnd();
        }

        return line;
    }

    private static bool HasTargetPhraseSignal(IEnumerable<string> tokens)
    {
        return tokens.Any(token =>
            token.Any(character => char.IsLetter(character) && character > '\u007f')
            || LooksLikeTargetPhraseToken(token)
            || Regex.IsMatch(token, @"[?!.,;:]$"));
    }

    private static bool LooksLikePronunciationTail(IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0 || tokens.Count > 8)
        {
            return false;
        }

        var normalizedTokens = tokens
            .Select(NormalizePronunciationToken)
            .Where(token => !string.IsNullOrWhiteSpace(token))
            .ToList();

        if (normalizedTokens.Count != tokens.Count)
        {
            return false;
        }

        if (tokens.Take(tokens.Count - 1).Any(token => Regex.IsMatch(token, @"[?!.,;:]$")))
        {
            return false;
        }

        if (!CanStartPronunciationTail(normalizedTokens[0]))
        {
            return false;
        }

        var pronunciationSignalCount = normalizedTokens.Count(token =>
            token.Contains('-')
            || token.Contains('/')
            || PronunciationTokens.Contains(token)
            || token.Split(['-', '/'], StringSplitOptions.RemoveEmptyEntries).Any(part => PronunciationTokens.Contains(part)));

        return pronunciationSignalCount > 0
            && normalizedTokens.All(token => Regex.IsMatch(token, @"^[a-z/'-]+$", RegexOptions.IgnoreCase))
            && (tokens.Count > 1 || pronunciationSignalCount == 1);
    }

    private static string NormalizePronunciationToken(string token)
    {
        return Regex.Replace(token.Trim(), @"^[^\p{L}]+|[^\p{L}'/-]+$", string.Empty);
    }

    private static bool CanStartPronunciationTail(string token)
    {
        return token.Contains('-')
            || PronunciationTokens.Contains(token)
            || string.Equals(token, "po", StringComparison.OrdinalIgnoreCase)
            || new[] { "m/f", "f/m", "inf" }.Contains(token, StringComparer.OrdinalIgnoreCase);
    }

    private static bool LooksLikeEnglishPrompt(string text)
    {
        var normalized = Regex.Replace(text.Trim(), @"[^\p{L}'\s]+", " ");
        var tokens = Regex.Matches(normalized, @"[\p{L}']+")
            .Select(match => match.Value)
            .ToList();
        if (tokens.Count == 0 || tokens.Count > 8)
        {
            return false;
        }

        var firstToken = tokens[0];
        if (new[] { "do", "does", "did", "yes", "no", "pardon", "what", "let's", "i" }
            .Contains(firstToken, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var englishSignal = tokens.Count(token =>
            new[] { "you", "your", "speak", "speaks", "understand", "understands", "english", "polish", "mean", "means", "little", "anyone" }
                .Contains(token, StringComparer.OrdinalIgnoreCase));

        return englishSignal >= 2;
    }

    private static bool LooksLikeTargetPhraseToken(string token)
    {
        var normalized = Regex.Replace(token.Trim(), @"^[^\p{L}]+|[^\p{L}]+$", string.Empty);
        return new[] { "co", "czy", "nie", "po", "tak" }
            .Contains(normalized, StringComparer.OrdinalIgnoreCase);
    }
}
