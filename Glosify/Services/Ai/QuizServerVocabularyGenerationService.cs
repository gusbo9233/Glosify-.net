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

    public async Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var request = new QuizServerRequest(
            word,
            sourceLanguage,
            targetLanguage,
            null,
            false);

        using var response = await _httpClient.PostAsJsonAsync("quizzes", request, JsonOptions, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Quiz server word-detail generation failed with status {StatusCode}: {Response}",
                (int)response.StatusCode,
                responseText);
            return null;
        }

        var serverResponse = DeserializeResponse(responseText);
        if (serverResponse == null)
        {
            _logger.LogWarning("Quiz server word-detail generation returned an unreadable response.");
            return null;
        }

        var serverWord = FindServerWord(word, translation, serverResponse.Words);
        var detail = FindServerWordDetail(serverWord?.WordDetailId, word, serverResponse.WordDetails);
        if (detail == null)
        {
            _logger.LogWarning("Quiz server word-detail generation returned no matching detail for {Word}.", word);
            return null;
        }

        return new GeneratedWordDetail
        {
            Properties = NormalizeProperties(detail.Properties),
            Variants = NormalizeVariants(detail.Variants),
            Explanation = CleanText(detail.Explanation),
            ExampleSentence = CleanText(detail.ExampleSentence),
            ExampleSentenceTranslation = FindSentenceTranslation(detail.ExampleSentence, serverResponse.Sentences)
        };
    }

    public async Task<QuizServerRepairQuizResult?> RepairQuizAsync(
        QuizServerRepairQuizData quizData,
        CancellationToken cancellationToken = default)
    {
        return await PostRepairAsync<QuizServerRepairQuizResult>(
            "repairs/quiz",
            new { quiz_data = quizData },
            cancellationToken);
    }

    public async Task<QuizServerRepairWordResult?> RepairWordAsync(
        QuizServerRepairQuizData quizData,
        string wordId,
        CancellationToken cancellationToken = default)
    {
        return await PostRepairAsync<QuizServerRepairWordResult>(
            "repairs/word",
            new { quiz_data = quizData, word_id = wordId },
            cancellationToken);
    }

    public async Task<QuizServerRepairSentenceResult?> RepairSentenceAsync(
        QuizServerRepairQuizData quizData,
        string sentenceText,
        CancellationToken cancellationToken = default)
    {
        return await PostRepairAsync<QuizServerRepairSentenceResult>(
            "repairs/sentence",
            new { quiz_data = quizData, sentence_text = sentenceText },
            cancellationToken);
    }

    private async Task<T?> PostRepairAsync<T>(
        string path,
        object request,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.PostAsJsonAsync(path, request, JsonOptions, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "Quiz server repair request to {Path} failed with status {StatusCode}: {Response}",
                path,
                (int)response.StatusCode,
                responseText);
            return default;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(responseText, JsonOptions);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Quiz server repair request to {Path} returned unreadable JSON.", path);
            return default;
        }
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
            true);

        using var response = await _httpClient.PostAsJsonAsync("quizzes", request, JsonOptions, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var serverMessage = ExtractServerError(responseText);
            _logger.LogWarning(
                "Quiz server vocabulary generation failed with status {StatusCode}: {Response}",
                (int)response.StatusCode,
                responseText);
            throw new QuizServerChunkException(serverMessage, response.StatusCode, responseText);
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
            var serverResponse = DeserializeResponse(json);
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

    private static QuizServerResponse? DeserializeResponse(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<QuizServerResponse>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static QuizServerWord? FindServerWord(
        string word,
        string translation,
        IReadOnlyList<QuizServerWord> words)
    {
        var cleanedWord = CleanText(word);
        var cleanedTranslation = CleanText(translation);

        return words.FirstOrDefault(candidate =>
                string.Equals(CleanText(candidate.Lemma), cleanedWord, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(cleanedTranslation)
                    || string.Equals(CleanText(candidate.Translation), cleanedTranslation, StringComparison.OrdinalIgnoreCase)))
            ?? words.FirstOrDefault(candidate =>
                string.Equals(CleanText(candidate.Lemma), cleanedWord, StringComparison.OrdinalIgnoreCase))
            ?? words.FirstOrDefault();
    }

    private static QuizServerWordDetail? FindServerWordDetail(
        string? wordDetailId,
        string word,
        IReadOnlyList<QuizServerWordDetail> details)
    {
        if (!string.IsNullOrWhiteSpace(wordDetailId))
        {
            var byId = details.FirstOrDefault(detail =>
                string.Equals(CleanText(detail.Id), CleanText(wordDetailId), StringComparison.OrdinalIgnoreCase));
            if (byId != null)
            {
                return byId;
            }
        }

        return details.FirstOrDefault(detail =>
                string.Equals(CleanText(detail.Id), CleanText(word), StringComparison.OrdinalIgnoreCase))
            ?? details.FirstOrDefault();
    }

    private static Dictionary<string, JsonElement> NormalizeProperties(Dictionary<string, JsonElement>? properties)
    {
        if (properties == null || properties.Count == 0)
        {
            return [];
        }

        return properties
            .Where(property => property.Value.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
            .ToDictionary(property => property.Key, property => property.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    private static List<GeneratedWordVariant> NormalizeVariants(IReadOnlyList<QuizServerWordDetailVariant>? variants)
    {
        if (variants == null || variants.Count == 0)
        {
            return [];
        }

        return variants
            .Select(variant => new GeneratedWordVariant
            {
                Form = CleanText(variant.Form),
                Tags = variant.Tags?
                    .Select(CleanText)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .ToList() ?? []
            })
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Form))
            .ToList();
    }

    private static string FindSentenceTranslation(
        string? exampleSentence,
        IReadOnlyList<QuizServerSentence> sentences)
    {
        if (string.IsNullOrWhiteSpace(exampleSentence))
        {
            return string.Empty;
        }

        return CleanText(sentences.FirstOrDefault(sentence =>
            string.Equals(CleanText(sentence.Text), CleanText(exampleSentence), StringComparison.OrdinalIgnoreCase))?.Translation);
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

    private static string ExtractServerError(string responseText)
    {
        if (!string.IsNullOrWhiteSpace(responseText))
        {
            try
            {
                using var document = JsonDocument.Parse(responseText);
                if (document.RootElement.TryGetProperty("detail", out var detail)
                    && detail.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(detail.GetString()))
                {
                    return detail.GetString()!;
                }
            }
            catch (JsonException)
            {
            }
        }

        return "The quiz server could not generate vocabulary from that input.";
    }

    private sealed record QuizServerRequest(
        [property: JsonPropertyName("input_text")] string InputText,
        [property: JsonPropertyName("source_language")] string SourceLanguage,
        [property: JsonPropertyName("target_language")] string TargetLanguage,
        [property: JsonPropertyName("quiz_name")] string? QuizName,
        [property: JsonPropertyName("skip_details")] bool SkipDetails);

    private sealed class QuizServerResponse
    {
        public List<QuizServerWord> Words { get; set; } = [];
        [JsonPropertyName("word_details")]
        public List<QuizServerWordDetail> WordDetails { get; set; } = [];
        public List<QuizServerSentence> Sentences { get; set; } = [];
    }

    private sealed class QuizServerWord
    {
        public string Lemma { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        [JsonPropertyName("word_detail_id")]
        public string WordDetailId { get; set; } = string.Empty;
    }

    private sealed class QuizServerWordDetail
    {
        public string Id { get; set; } = string.Empty;
        public Dictionary<string, JsonElement> Properties { get; set; } = [];
        [JsonPropertyName("example_sentence")]
        public string ExampleSentence { get; set; } = string.Empty;
        public string Explanation { get; set; } = string.Empty;
        public List<QuizServerWordDetailVariant> Variants { get; set; } = [];
    }

    private sealed class QuizServerWordDetailVariant
    {
        public string Form { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
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
