using Glosify.Models;
using Google.GenAI;
using Google.GenAI.Types;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Glosify.Services;

public class AiWordGenerationService : IAiWordGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<AiWordGenerationService> _logger;

    public AiWordGenerationService(
        IOptions<GeminiOptions> options,
        ILogger<AiWordGenerationService> logger)
    {
        var settings = options.Value;
        if (string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set Gemini:ApiKey via configuration, user secrets, or the Gemini__ApiKey environment variable.");
        }
        _apiKey = settings.ApiKey;
        _model = settings.Model;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage)
    {
        var json = await GenerateWordsWithAssistant(input, knownLanguage, targetLanguage);
        if (!ValidateResponse(json))
        {
            _logger.LogWarning("Gemini word-extraction response failed validation for {KnownLanguage}->{TargetLanguage}", knownLanguage, targetLanguage);
            throw new InvalidOperationException("The AI assistant returned an unexpected response format.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, GeneratedWord>>(json) ?? [];
    }

    public async Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        var json = await GenerateWordDetailWithAssistant(
            word.Trim(),
            translation.Trim(),
            knownLanguage,
            targetLanguage);

        if (!ValidateWordDetailResponse(json))
        {
            _logger.LogWarning("Gemini word-detail response failed validation for word {Word} ({KnownLanguage}->{TargetLanguage})", word, knownLanguage, targetLanguage);
            throw new InvalidOperationException("The AI assistant returned an unexpected word detail response format.");
        }

        return JsonSerializer.Deserialize<GeneratedWordDetail>(json, JsonOptions);
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var json = await GenerateWordsWithAssistant(
            input,
            knownLanguage,
            targetLanguage,
            sourceSentences);
        if (!ValidateResponse(json))
        {
            _logger.LogWarning("Gemini word-extraction response failed validation for {KnownLanguage}->{TargetLanguage} with {SentenceCount} source sentences", knownLanguage, targetLanguage, sourceSentences.Count);
            throw new InvalidOperationException("The AI assistant returned an unexpected response format.");
        }

        return JsonSerializer.Deserialize<Dictionary<string, GeneratedWord>>(json) ?? [];
    }

    public bool ValidateResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;

            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                var val = entry.Value;
                if (val.ValueKind != JsonValueKind.Object) return false;
                if (!val.TryGetProperty("translation", out var translation) || translation.ValueKind != JsonValueKind.String) return false;
                if (!val.TryGetProperty("example_sentence", out var example) || example.ValueKind != JsonValueKind.String) return false;
                if (!val.TryGetProperty("example_sentence_translation", out var exampleTranslation) || exampleTranslation.ValueKind != JsonValueKind.String) return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private async Task<string> GenerateWordsWithAssistant(string input, string knownLanguage, string targetLanguage)
    {
        var prompt = BuildWordExtractionPrompt(input, knownLanguage, targetLanguage);
        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: new GenerateContentConfig { ResponseMimeType = "application/json" }
        );

        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    private async Task<string> GenerateWordDetailWithAssistant(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        var prompt = BuildWordDetailPrompt(word, translation, knownLanguage, targetLanguage);
        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: new GenerateContentConfig { ResponseMimeType = "application/json" }
        );

        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    private async Task<string> GenerateWordsWithAssistant(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var prompt = BuildWordExtractionPrompt(input, knownLanguage, targetLanguage, sourceSentences);
        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: new GenerateContentConfig { ResponseMimeType = "application/json" }
        );

        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    private string BuildWordExtractionPrompt(string input, string knownLanguage, string targetLanguage)
    {
        var candidateWords = ExtractCandidateWords(input);
        var candidates = string.Join("\n", candidateWords.Select(word => $"- {word}"));
        var extractedSourceSentences = ExtractSourceSentences(input);
        var useSourceSentences = ShouldUseSourceSentences(extractedSourceSentences);
        var sourceSentences = string.Join("\n", extractedSourceSentences.Select(sentence => $"- {sentence}"));
        var exampleSentenceRule = useSourceSentences
            ? "- \"example_sentence\": the exact source sentence or phrase from the list below that contains the word"
            : $"- \"example_sentence\": a natural example sentence in {targetLanguage} using the word";
        var exampleSentenceTranslationRule = useSourceSentences
            ? $"- \"example_sentence_translation\": the {knownLanguage} translation of that exact source sentence or phrase"
            : $"- \"example_sentence_translation\": the {knownLanguage} translation of the example sentence";

        return $@"
The user knows {knownLanguage} and is learning {targetLanguage}.
Extract vocabulary from the input below and return a JSON object.

Rules:
- Output MUST be valid JSON only. No explanations, no extra text.
- Include EVERY distinct candidate word listed below.
- Preserve each candidate word exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities.
- Include short/common words too, such as auxiliaries, prepositions, and adverbs.
- Do not merge separate candidate words into phrases.
- Each key is one candidate word in {targetLanguage}.
- Each value is an object with:
- ""translation"": the {knownLanguage} translation of the word
{exampleSentenceRule}
{exampleSentenceTranslationRule}

Format:
{{
""word1"": {{
    ""translation"": ""..."",
    ""example_sentence"": ""..."",
    ""example_sentence_translation"": ""...""
}}
}}

Candidate words:
{candidates}

Source sentences:
{sourceSentences}

Input:
{input}";
    }

    private static string BuildWordDetailPrompt(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        return $@"
The user knows {knownLanguage} and is learning {targetLanguage}.
Create grammatical word detail for this vocabulary item and return a JSON object.

Vocabulary:
- word: {word}
- {knownLanguage} translation: {translation}

Rules:
- Output MUST be valid JSON only. No explanations, no extra text.
- Do not invent rare or archaic forms unless they are central to normal use.
- Keep values concise and learner-friendly.
- ""properties"" MUST be an object. Include ""pos"" using one of: noun, verb, article, adjective, pronoun, adverb, preposition, conjunction, numeral, interjection.
- Add useful grammatical properties when relevant, such as gender, number, case, tense, aspect, mood, person, comparative, superlative, tags.
- ""variants"" MUST be an array of objects with ""form"" and ""tags"".
- Variant tags should be lowercase grammar tags such as nominative, genitive, singular, plural, infinitive, present, past, first-person.
- Include only forms you are confident are correct. Use an empty array if no variants are useful.
- ""explanation"" MUST be a concise {knownLanguage} explanation of the word and its usage.
- ""example_sentence"" MUST be a natural {targetLanguage} sentence using the word.

Format:
{{
  ""properties"": {{
    ""pos"": ""noun""
  }},
  ""variants"": [
    {{
      ""form"": ""..."",
      ""tags"": [""...""]
    }}
  ],
  ""explanation"": ""..."",
  ""example_sentence"": ""...""
}}";
    }

    private string BuildWordExtractionPrompt(
        string input,
        string knownLanguage,
        string targetLanguage,
        IReadOnlyList<string> sourceSentences)
    {
        var candidateWords = ExtractCandidateWords(input);
        var candidates = string.Join("\n", candidateWords.Select(word => $"- {word}"));
        var sentences = string.Join("\n", sourceSentences.Select(sentence => $"- {sentence}"));
        var useSourceSentences = ShouldUseSourceSentences(sourceSentences);
        var exampleSentenceRule = useSourceSentences
            ? "- \"example_sentence\": the exact source sentence or phrase from the list below that contains the word"
            : $"- \"example_sentence\": a natural example sentence in {targetLanguage} using the word";
        var exampleSentenceTranslationRule = useSourceSentences
            ? $"- \"example_sentence_translation\": the {knownLanguage} translation of that exact source sentence or phrase"
            : $"- \"example_sentence_translation\": the {knownLanguage} translation of the example sentence";

        return $@"
The user knows {knownLanguage} and is learning {targetLanguage}.
Extract vocabulary from the input below and return a JSON object.

Rules:
- Output MUST be valid JSON only. No explanations, no extra text.
- Include EVERY distinct candidate word listed below.
- Preserve each candidate word exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities.
- Include short/common words too, such as auxiliaries, prepositions, and adverbs.
- Do not merge separate candidate words into phrases.
- Each key is one candidate word in {targetLanguage}.
- Each value is an object with:
- ""translation"": the {knownLanguage} translation of the word
{exampleSentenceRule}
{exampleSentenceTranslationRule}

Format:
{{
""word1"": {{
    ""translation"": ""..."",
    ""example_sentence"": ""..."",
    ""example_sentence_translation"": ""...""
}}
}}

Candidate words:
{candidates}

Source sentences:
{sentences}

Input:
{input}";
    }

    private static IReadOnlyList<string> ExtractCandidateWords(string input)
    {
        return Regex.Matches(input, @"[\p{L}\p{M}]+(?:[''][\p{L}\p{M}]+)?")
            .Select(match => match.Value.Trim())
            .Where(word => !string.IsNullOrWhiteSpace(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractSourceSentences(string input)
    {
        return Regex.Split(input, @"(?<=[.!?])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static bool ShouldUseSourceSentences(IReadOnlyList<string> sourceSentences)
    {
        if (sourceSentences.Count == 0)
        {
            return false;
        }

        return sourceSentences.Any(sentence => ExtractCandidateWords(sentence).Count > 2);
    }

    private static bool ValidateWordDetailResponse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("variants", out var variants) || variants.ValueKind != JsonValueKind.Array) return false;
            if (!root.TryGetProperty("explanation", out var explanation) || explanation.ValueKind != JsonValueKind.String) return false;
            if (!root.TryGetProperty("example_sentence", out var example) || example.ValueKind != JsonValueKind.String) return false;

            foreach (var variant in variants.EnumerateArray())
            {
                if (variant.ValueKind != JsonValueKind.Object) return false;
                if (!variant.TryGetProperty("form", out var form) || form.ValueKind != JsonValueKind.String) return false;
                if (!variant.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Array) return false;
                if (tags.EnumerateArray().Any(tag => tag.ValueKind != JsonValueKind.String)) return false;
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

}
