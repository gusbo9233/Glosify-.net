using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Glosify.Services;

public sealed class OpenAiVocabularyGenerationService : IOpenAiVocabularyGenerationService
{
    private const string SystemInstruction = """
You create simple vocabulary quiz data from pasted learner text.
Return only clean structured JSON. No markdown, no explanations, no symbols copied from notes.
""";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly OpenAiOptions _options;
    private readonly ILogger<OpenAiVocabularyGenerationService> _logger;

    public OpenAiVocabularyGenerationService(
        HttpClient httpClient,
        IOptions<OpenAiOptions> options,
        ILogger<OpenAiVocabularyGenerationService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences,
        CancellationToken cancellationToken = default)
    {
        EnsureConfigured();

        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var prompt = BuildPrompt(cleanedInput, knownLanguage, targetLanguage, sourceSentences);
        var request = CreateRequest(prompt);

        using var message = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses")
        {
            Content = new StringContent(JsonSerializer.Serialize(request, JsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        using var response = await _httpClient.SendAsync(message, cancellationToken);
        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogWarning(
                "OpenAI vocabulary generation failed with status {StatusCode}: {Response}",
                (int)response.StatusCode,
                responseJson);
            throw new InvalidOperationException("The OpenAI assistant returned an error.");
        }

        var outputText = ExtractOutputText(responseJson);
        var words = NormalizeGeneratedWords(outputText);
        if (words.Count == 0)
        {
            _logger.LogWarning("OpenAI vocabulary generation returned no usable items.");
            throw new InvalidOperationException("The OpenAI assistant returned an unexpected response format.");
        }

        return words;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "OpenAI API key is not configured. Set OpenAI:ApiKey via user secrets or OPENAI_API_KEY.");
        }
    }

    private object CreateRequest(string prompt)
    {
        return new
        {
            model = _options.Model,
            instructions = SystemInstruction,
            input = prompt,
            max_output_tokens = 6_000,
            text = new
            {
                format = new
                {
                    type = "json_schema",
                    name = "vocabulary_words",
                    strict = true,
                    schema = new
                    {
                        type = "object",
                        additionalProperties = false,
                        properties = new
                        {
                            words = new
                            {
                                type = "array",
                                minItems = 1,
                                items = new
                                {
                                    type = "object",
                                    additionalProperties = false,
                                    properties = new
                                    {
                                        word = new { type = "string" },
                                        translation = new { type = "string" },
                                        example_sentence = new { type = "string" },
                                        example_sentence_translation = new { type = "string" }
                                    },
                                    required = new[]
                                    {
                                        "word",
                                        "translation",
                                        "example_sentence",
                                        "example_sentence_translation"
                                    }
                                }
                            }
                        },
                        required = new[] { "words" }
                    }
                }
            }
        };
    }

    private static string BuildPrompt(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var cleanedSourceSentences = VocabularyInputCleaner.CleanSourceSentences(sourceSentences);
        var sourceSentenceText = string.Join("\n", cleanedSourceSentences.Select(sentence => $"- {sentence}"));

        return $@"
The user knows {knownLanguage} and is learning {targetLanguage}.
Create vocabulary and sentence quiz data from the pasted text.

Rules:
- Extract useful {targetLanguage} words from the pasted text.
- Use plain words and plain sentences.
- Each word must be a single clean {targetLanguage} word copied from the text.
- Do not include punctuation, brackets, parentheses, slashes, bullets, markdown, pronunciation text, labels, grammar notes, language codes, or special helper symbols.
- Do not include source-language prompt text as target-language vocabulary.
- For every word, provide a concise {knownLanguage} translation.
- For every word, provide one natural {targetLanguage} example sentence and its {knownLanguage} translation.
- Prefer clean sentences from the pasted text when they are already natural. Otherwise write a short natural sentence.

Source sentences, if useful:
{sourceSentenceText}

Pasted text:
{input}";
    }

    private static string ExtractOutputText(string responseJson)
    {
        using var document = JsonDocument.Parse(responseJson);
        var root = document.RootElement;

        if (root.TryGetProperty("output_text", out var outputText)
            && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString() ?? string.Empty;
        }

        if (!root.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var item in output.EnumerateArray())
        {
            if (!item.TryGetProperty("content", out var content)
                || content.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var contentItem in content.EnumerateArray())
            {
                if (contentItem.TryGetProperty("text", out var text)
                    && text.ValueKind == JsonValueKind.String)
                {
                    parts.Add(text.GetString() ?? string.Empty);
                }
            }
        }

        return string.Join("\n", parts.Where(part => !string.IsNullOrWhiteSpace(part)));
    }

    private static IReadOnlyDictionary<string, GeneratedWord> NormalizeGeneratedWords(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("words", out var words)
                || words.ValueKind != JsonValueKind.Array)
            {
                return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
            }

            var normalized = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in words.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var word = CleanWord(ReadString(item, "word"));
                var translation = CleanText(ReadString(item, "translation"));
                var exampleSentence = CleanText(ReadString(item, "example_sentence"));
                var exampleTranslation = CleanText(ReadString(item, "example_sentence_translation"));

                if (string.IsNullOrWhiteSpace(word)
                    || string.IsNullOrWhiteSpace(translation)
                    || ContainsNoise(word)
                    || ContainsNoise(translation)
                    || ContainsNoise(exampleSentence)
                    || ContainsNoise(exampleTranslation))
                {
                    continue;
                }

                normalized[word] = new GeneratedWord
                {
                    Translation = translation,
                    ExampleSentence = exampleSentence,
                    ExampleSentenceTranslation = exampleTranslation
                };
            }

            return normalized;
        }
        catch (JsonException)
        {
            return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static string? ReadString(JsonElement element, string name)
    {
        return element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string CleanWord(string? value)
    {
        var cleaned = CleanText(value);
        return cleaned.Any(char.IsWhiteSpace) ? string.Empty : cleaned;
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = System.Text.RegularExpressions.Regex.Replace(value, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"[/\\|*_`~#<>]+", " ");
        cleaned = System.Text.RegularExpressions.Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim(' ', ',', '.', ';', ':', '-', '"', '\'');
    }

    private static bool ContainsNoise(string value)
    {
        return System.Text.RegularExpressions.Regex.IsMatch(value, @"[()[\]{}\\/|*_`~#<>]");
    }
}
