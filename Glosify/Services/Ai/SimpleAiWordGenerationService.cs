using Glosify.Models;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public class SimpleAiWordGenerationService : IAiWordGenerationService
{
    private const string SystemInstruction = """
You create simple vocabulary quiz data from pasted learner text.
Return only clean JSON. No markdown, no explanations, no symbols copied from notes.
""";

    private readonly string _apiKey;
    private readonly string _model;
    private readonly AiWordGenerationService _detailedService;
    private readonly ILogger<SimpleAiWordGenerationService> _logger;

    public SimpleAiWordGenerationService(
        IOptions<GeminiOptions> options,
        AiWordGenerationService detailedService,
        ILogger<SimpleAiWordGenerationService> logger)
    {
        var settings = options.Value;
        _apiKey = settings.ApiKey;
        _model = settings.Model;
        _detailedService = detailedService;
        _logger = logger;
    }

    public Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage)
    {
        return GenerateWordsFromTextAsync(input, knownLanguage, targetLanguage, []);
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var prompt = BuildSimpleVocabularyPrompt(
            cleanedInput,
            knownLanguage,
            targetLanguage,
            sourceSentences);
        var json = await GenerateWithAssistantAsync(prompt);
        var words = NormalizeGeneratedWords(json);

        if (words.Count == 0)
        {
            _logger.LogWarning(
                "Simple AI vocabulary generation returned no usable items for {KnownLanguage}->{TargetLanguage}",
                knownLanguage,
                targetLanguage);
            throw new InvalidOperationException("The AI assistant returned an unexpected response format.");
        }

        return words;
    }

    public Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        return _detailedService.GenerateWordDetailAsync(word, translation, knownLanguage, targetLanguage);
    }

    public bool ValidateResponse(string json)
    {
        return NormalizeGeneratedWords(json).Count > 0;
    }

    private async Task<string> GenerateWithAssistantAsync(string prompt)
    {
        EnsureConfigured();

        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: new GenerateContentConfig
            {
                SystemInstruction = new Content
                {
                    Parts = [new Part { Text = SystemInstruction }]
                },
                ResponseMimeType = "application/json",
                ResponseSchema = CreateVocabularyResponseSchema(),
                ThinkingConfig = new ThinkingConfig
                {
                    ThinkingLevel = ThinkingLevel.High
                },
                Temperature = 0.1f,
                MaxOutputTokens = 6_000
            });

        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    private void EnsureConfigured()
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set Gemini:ApiKey via configuration, user secrets, Gemini__ApiKey, or GEMINI_API_KEY.");
        }
    }

    private static Schema CreateVocabularyResponseSchema()
    {
        var generatedWordSchema = new Schema
        {
            Type = Google.GenAI.Types.Type.Object,
            Properties = new Dictionary<string, Schema>
            {
                ["word"] = new()
                {
                    Type = Google.GenAI.Types.Type.String,
                    Description = "Single clean target-language vocabulary word."
                },
                ["translation"] = new()
                {
                    Type = Google.GenAI.Types.Type.String,
                    Description = "Plain translation of the vocabulary word."
                },
                ["example_sentence"] = new()
                {
                    Type = Google.GenAI.Types.Type.String,
                    Description = "Plain target-language sentence using the vocabulary word."
                },
                ["example_sentence_translation"] = new()
                {
                    Type = Google.GenAI.Types.Type.String,
                    Description = "Plain translation of the example sentence."
                }
            },
            PropertyOrdering =
            [
                "word",
                "translation",
                "example_sentence",
                "example_sentence_translation"
            ],
            Required =
            [
                "word",
                "translation",
                "example_sentence",
                "example_sentence_translation"
            ]
        };

        return new Schema
        {
            Type = Google.GenAI.Types.Type.Object,
            Properties = new Dictionary<string, Schema>
            {
                ["words"] = new()
                {
                    Type = Google.GenAI.Types.Type.Array,
                    Description = "Generated vocabulary entries.",
                    Items = generatedWordSchema,
                    MinItems = 1
                }
            },
            Required = ["words"],
            PropertyOrdering = ["words"]
        };
    }

    private static string BuildSimpleVocabularyPrompt(
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

Return the existing JSON format:
{{
  ""word"": {{
    ""translation"": ""{knownLanguage} translation of the word"",
    ""example_sentence"": ""plain {targetLanguage} sentence using the word"",
    ""example_sentence_translation"": ""{knownLanguage} translation of the sentence""
  }}
}}

Structured output may wrap entries like this, which the app also accepts:
{{
  ""words"": [
    {{
      ""word"": ""..."",
      ""translation"": ""..."",
      ""example_sentence"": ""..."",
      ""example_sentence_translation"": ""...""
    }}
  ]
}}

Rules:
- Output valid JSON only.
- Use plain words and plain sentences.
- Extract useful {targetLanguage} words from the pasted text.
- JSON keys must be single clean {targetLanguage} words copied from the text.
- Do not include punctuation, brackets, parentheses, slashes, bullets, markdown, pronunciation text, labels, grammar notes, language codes, or special helper symbols in words, sentences, or translations.
- Do not include source-language prompt text as target-language vocabulary.
- For every word, provide a concise {knownLanguage} translation.
- For every word, provide one natural {targetLanguage} example sentence and its {knownLanguage} translation.
- Prefer clean sentences from the pasted text when they are already natural. Otherwise write a short natural sentence.
- Do not add explanations or fields outside translation, example_sentence, and example_sentence_translation.

Source sentences, if useful:
{sourceSentenceText}

Pasted text:
{input}";
    }

    private static IReadOnlyDictionary<string, GeneratedWord> NormalizeGeneratedWords(string json)
    {
        if (!TryReadGeneratedWords(json, out var generated))
        {
            return new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        }

        var normalized = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        foreach (var (word, generatedWord) in generated)
        {
            var cleanWord = CleanWord(word);
            var cleanTranslation = CleanText(generatedWord.Translation);
            var cleanSentence = CleanText(generatedWord.ExampleSentence);
            var cleanSentenceTranslation = CleanText(generatedWord.ExampleSentenceTranslation);

            if (string.IsNullOrWhiteSpace(cleanWord)
                || string.IsNullOrWhiteSpace(cleanTranslation)
                || ContainsNoise(cleanWord)
                || ContainsNoise(cleanTranslation)
                || ContainsNoise(cleanSentence)
                || ContainsNoise(cleanSentenceTranslation))
            {
                continue;
            }

            normalized[cleanWord] = new GeneratedWord
            {
                Translation = cleanTranslation,
                ExampleSentence = cleanSentence,
                ExampleSentenceTranslation = cleanSentenceTranslation
            };
        }

        return normalized;
    }

    private static bool TryReadGeneratedWords(
        string json,
        out Dictionary<string, GeneratedWord> generated)
    {
        generated = [];

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (doc.RootElement.TryGetProperty("words", out var words) && words.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in words.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object)
                    {
                        continue;
                    }

                    var word = ReadFirstString(item, "lemma", "word", "token", "text");
                    var generatedWord = ReadGeneratedWord(item);
                    if (!string.IsNullOrWhiteSpace(word) && !string.IsNullOrWhiteSpace(generatedWord.Translation))
                    {
                        generated[word.Trim()] = generatedWord;
                    }
                }

                return generated.Count > 0;
            }

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var generatedWord = ReadGeneratedWord(entry.Value);
                if (!string.IsNullOrWhiteSpace(generatedWord.Translation))
                {
                    generated[entry.Name.Trim()] = generatedWord;
                }
            }

            return generated.Count > 0;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static GeneratedWord ReadGeneratedWord(JsonElement element)
    {
        return new GeneratedWord
        {
            Translation = ReadFirstString(element, "translation"),
            ExampleSentence = ReadFirstString(element, "example_sentence", "exampleSentence", "sentence"),
            ExampleSentenceTranslation = ReadFirstString(
                element,
                "example_sentence_translation",
                "exampleSentenceTranslation",
                "sentence_translation")
        };
    }

    private static string? ReadFirstString(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
            {
                return property.GetString();
            }
        }

        return null;
    }

    private static string CleanWord(string? value)
    {
        var cleaned = CleanText(value);
        return Regex.IsMatch(cleaned, @"\s") ? string.Empty : cleaned;
    }

    private static string CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*", " ");
        cleaned = Regex.Replace(cleaned, @"[/\\|*_`~#<>]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ");
        return cleaned.Trim(' ', ',', '.', ';', ':', '-', '"', '\'');
    }

    private static bool ContainsNoise(string value)
    {
        return Regex.IsMatch(value, @"[()[\]{}\\/|*_`~#<>]");
    }
}
