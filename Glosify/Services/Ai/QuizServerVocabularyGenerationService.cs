using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public sealed class QuizServerVocabularyGenerationService : IQuizServerVocabularyGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);
    private const int MaxTokensPerRequest = 12;

    private readonly HttpClient _httpClient;
    private readonly ILogger<QuizServerVocabularyGenerationService> _logger;

    public QuizServerVocabularyGenerationService(
        HttpClient httpClient,
        ILogger<QuizServerVocabularyGenerationService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string sourceLanguage,
        string targetLanguage,
        string? quizName,
        CancellationToken cancellationToken = default)
    {
        var chunks = SplitInput(input, MaxTokensPerRequest);
        if (chunks.Count > 1)
        {
            _logger.LogInformation(
                "Splitting quiz server generation into {ChunkCount} requests to avoid long-running upstream responses.",
                chunks.Count);
        }

        var merged = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        var failedChunks = 0;
        for (var index = 0; index < chunks.Count; index++)
        {
            var chunkName = chunks.Count == 1 || string.IsNullOrWhiteSpace(quizName)
                ? quizName
                : $"{quizName} ({index + 1}/{chunks.Count})";
            IReadOnlyDictionary<string, GeneratedWord> chunkWords;
            try
            {
                chunkWords = await GenerateChunkAsync(
                    chunks[index],
                    sourceLanguage,
                    targetLanguage,
                    chunkName,
                    cancellationToken);
            }
            catch (Exception ex) when (chunks.Count > 1 && IsSkippableChunkFailure(ex))
            {
                failedChunks++;
                _logger.LogWarning(
                    ex,
                    "Skipping quiz server chunk {ChunkNumber}/{ChunkCount}; no usable vocabulary was returned.",
                    index + 1,
                    chunks.Count);
                continue;
            }

            foreach (var (lemma, word) in chunkWords)
            {
                merged[lemma] = word;
            }
        }

        if (merged.Count == 0 && failedChunks > 0)
        {
            throw new InvalidOperationException($"The quiz server could not find {targetLanguage} vocabulary in the submitted text.");
        }

        if (failedChunks > 0)
        {
            _logger.LogInformation(
                "Quiz server generation completed with {FailedChunkCount} skipped chunk(s) and {WordCount} usable word(s).",
                failedChunks,
                merged.Count);
        }

        return merged;
    }

    private async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateChunkAsync(
        string input,
        string sourceLanguage,
        string targetLanguage,
        string? quizName,
        CancellationToken cancellationToken)
    {
        var request = new QuizServerRequest(
            input,
            sourceLanguage,
            targetLanguage,
            string.IsNullOrWhiteSpace(quizName) ? null : quizName,
            "gemini",
            true);

        using var response = await _httpClient.PostAsJsonAsync("quizzes", request, JsonOptions, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Quiz server vocabulary generation failed with status {StatusCode}: {Response}",
                (int)response.StatusCode,
                responseText);
            throw new QuizServerChunkException($"The quiz server returned HTTP {(int)response.StatusCode}.", response.StatusCode, responseText);
        }

        var words = NormalizeGeneratedWords(responseText);
        if (words.Count == 0)
        {
            _logger.LogWarning("Quiz server vocabulary generation returned no usable words.");
            throw new QuizServerChunkException("The quiz server returned no usable words.", response.StatusCode, responseText);
        }

        return words;
    }

    private static bool IsSkippableChunkFailure(Exception ex)
    {
        return ex is QuizServerChunkException;
    }

    private static IReadOnlyList<string> SplitInput(string input, int maxTokensPerRequest)
    {
        var segments = Regex.Split(input, @"(?<=[.!?])\s+|\r?\n+")
            .Select(segment => segment.Trim())
            .Where(segment => !string.IsNullOrWhiteSpace(segment))
            .ToList();

        if (segments.Count == 0)
        {
            return [input];
        }

        var chunks = new List<string>();
        var current = new List<string>();
        var currentTokenCount = 0;

        foreach (var segment in segments)
        {
            var tokenCount = CountTokens(segment);
            if (current.Count > 0 && currentTokenCount + tokenCount > maxTokensPerRequest)
            {
                chunks.Add(string.Join(" ", current));
                current.Clear();
                currentTokenCount = 0;
            }

            current.Add(segment.Trim());
            currentTokenCount += tokenCount;
        }

        if (current.Count > 0)
        {
            chunks.Add(string.Join(" ", current));
        }

        return chunks.Count == 0 ? [input] : chunks;
    }

    private static int CountTokens(string value)
    {
        return WordPattern.Matches(value).Count;
    }

    internal static IReadOnlyDictionary<string, GeneratedWord> NormalizeGeneratedWords(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            var serverResponse = JsonSerializer.Deserialize<QuizServerResponse>(json, JsonOptions);
            if (serverResponse?.Words is not { Count: > 0 } serverWords)
            {
                return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
            }

            var sentences = serverResponse.Sentences ?? [];
            var normalized = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
            foreach (var word in serverWords)
            {
                var lemma = CleanText(word.Lemma);
                var translation = CleanText(word.Translation);
                if (string.IsNullOrWhiteSpace(lemma) || string.IsNullOrWhiteSpace(translation))
                {
                    continue;
                }

                var sentence = FindSentenceForWord(lemma, sentences);
                normalized[lemma] = new GeneratedWord
                {
                    Translation = translation,
                    ExampleSentence = CleanText(sentence?.Text),
                    ExampleSentenceTranslation = CleanText(sentence?.Translation)
                };
            }

            return normalized;
        }
        catch (JsonException)
        {
            return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static QuizServerSentence? FindSentenceForWord(
        string lemma,
        IReadOnlyList<QuizServerSentence> sentences)
    {
        return sentences.FirstOrDefault(sentence =>
            !string.IsNullOrWhiteSpace(sentence.Text)
            && sentence.Text.Contains(lemma, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private sealed record QuizServerRequest(
        [property: JsonPropertyName("input_text")] string InputText,
        [property: JsonPropertyName("source_language")] string SourceLanguage,
        [property: JsonPropertyName("target_language")] string TargetLanguage,
        [property: JsonPropertyName("quiz_name")] string? QuizName,
        [property: JsonPropertyName("llm_provider")] string LlmProvider,
        [property: JsonPropertyName("skip_details")] bool SkipDetails);

    private sealed class QuizServerResponse
    {
        public List<QuizServerWord> Words { get; set; } = [];
        public List<QuizServerSentence> Sentences { get; set; } = [];
    }

    private sealed class QuizServerWord
    {
        public string Lemma { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
    }

    private sealed class QuizServerSentence
    {
        public string Text { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
    }

    private sealed class QuizServerChunkException : InvalidOperationException
    {
        public QuizServerChunkException(string message, System.Net.HttpStatusCode statusCode, string responseText)
            : base(message)
        {
            StatusCode = statusCode;
            ResponseText = responseText;
        }

        public System.Net.HttpStatusCode StatusCode { get; }
        public string ResponseText { get; }
    }
}
