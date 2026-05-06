using Glosify.Models;
using Glosify.Models.LanguageConfig;
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

    private static readonly HashSet<string> AppActionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "open",
        "delete",
        "translate"
    };

    private readonly string _apiKey;
    private readonly string _model;
    private readonly ILogger<AiWordGenerationService> _logger;

    public AiWordGenerationService(
        IOptions<GeminiOptions> options,
        ILogger<AiWordGenerationService> logger)
    {
        var settings = options.Value;
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
        EnsureConfigured();

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
        EnsureConfigured();

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
        EnsureConfigured();

        var prompt = BuildWordExtractionPrompt(input, knownLanguage, targetLanguage, sourceSentences);
        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: new GenerateContentConfig { ResponseMimeType = "application/json" }
        );

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
- Extract only vocabulary items that are actually written in {targetLanguage}.
- The candidate list is a hint list, not a command to include everything.
- Ignore words from {knownLanguage}, UI labels, buttons, menu text, metadata, and helper text.
- Ignore pronunciation notes and phonetic hints. Do not extract words from phrases such as ""pronunciation of"", ""pronounced"", ""sounds like"", ""say it like"", ""IPA"", or romanization notes.
- If a line pairs a word with a pronunciation hint, include only the real {targetLanguage} vocabulary word, never the pronunciation helper words.
- Preserve included candidate words exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities only when they are in {targetLanguage}.
- Include short/common words only when they are real {targetLanguage} vocabulary in the pasted material, not UI or helper text.
- Do not merge separate candidate words into phrases.
- Each key is one included candidate word in {targetLanguage}.
- Each value is an object with:
- ""translation"": the {knownLanguage} translation of the word
{exampleSentenceRule}
{exampleSentenceTranslationRule}
- ""example_sentence"" MUST be a complete {targetLanguage} sentence or natural phrase with at least two words. It must not be a definition or explanation.
- ""example_sentence_translation"" MUST translate the example sentence. It must not be copied into ""example_sentence"".

Format:
{{
""word1"": {{
    ""translation"": ""..."",
    ""example_sentence"": ""..."",
    ""example_sentence_translation"": ""...""
}}
}}

Possible candidate words:
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
        var grammarGuidance = BuildGrammarGuidance(targetLanguage);

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
- Variant tags MUST be separate lowercase grammar tags, not combined labels. Use tags such as nominative, genitive, dative, accusative, singular, plural, infinitive, present, past, first-person, second-person, third-person, indicative, imperative, participle.
- Include the canonical form itself as a variant when it has meaningful tags.
- For inflected nouns, verbs, adjectives, pronouns, articles, and numerals, include the common learner forms you are confident are correct. Use an empty array only if no variants are genuinely useful.
{grammarGuidance}
- ""explanation"" MUST be a concise {knownLanguage} explanation of the word and its usage.
- ""example_sentence"" MUST be a natural {targetLanguage} sentence using the word, with at least two words.
- ""example_sentence"" MUST NOT be the explanation, translation, a definition, or a one-word answer.
- ""explanation"" and ""example_sentence"" MUST be different strings in different languages.

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

    private static string BuildGrammarGuidance(string targetLanguage)
    {
        return LanguageResolver.ResolveLangCode(targetLanguage) switch
        {
            "de" => @"- German nouns: include nominative, genitive, dative, and accusative forms for singular and plural when applicable. Tag each with the case plus singular or plural.
- German articles: include masculine, feminine, neuter, and plural case forms. Tag each with the case, gender when singular, and singular or plural.
- German verbs: include infinitive, present indicative, simple past indicative, past participle, present participle, and common imperative forms. Use tags like present, past, indicative, participle, imperative, first-person, second-person, third-person, singular, plural.",

            "et" => @"- Estonian nouns and adjectives: include nominative, genitive, partitive, illative, inessive, elative, allative, adessive, ablative, translative, terminative, essive, abessive, and comitative singular forms when applicable. Also include plural nominative, plural genitive, and plural partitive when confident.
- Estonian verbs: include ma-infinitive and da-infinitive as tags exactly named ma-infinitive and da-infinitive. Include present and past indicative person/number forms plus common participles, conditional, imperative, quotative, and impersonal forms when confident.",

            "pl" => @"- Polish nouns: include nominative, genitive, dative, accusative, instrumental, locative, and vocative forms for singular and plural when applicable. Tag each with the case plus singular or plural.
- Polish adjectives: include nominative masculine, feminine, and neuter singular; nominative masculine-personal and non-masculine-personal plural; comparative and superlative when applicable.
- Polish verbs: include infinitive and aspect tags imperfective or perfective. For present or future person forms, include the tag non-past plus person and number. For past forms, include past with masculine, feminine, neuter, masculine-personal, or non-masculine-personal as applicable.",

            "uk" => @"- Ukrainian nouns: include nominative, genitive, dative, accusative, instrumental, locative, and vocative forms for singular and plural when applicable. Tag each with the case plus singular or plural.
- Ukrainian adjectives: include nominative masculine, feminine, and neuter singular; nominative plural; comparative and superlative when applicable.
- Ukrainian pronouns: if the vocabulary word is an oblique or possessive pronoun form such as його, її, йому, ним, мені, тебе, or нас, infer the base pronoun/paradigm and include its common nominative, genitive, dative, accusative, instrumental, and locative forms. Tag each variant with its case, and add person, number, gender, and possessive tags when useful. It is acceptable for the original surface form to appear in multiple cases when Ukrainian uses the same form.
- Ukrainian verbs: include infinitive and aspect tags imperfective or perfective. For present or future person forms, include the tag non-past plus person and number. For past forms, include past with masculine, feminine, neuter, or plural as applicable. Include common imperative forms when confident.",

            _ => @"- For case-based languages, include common noun, pronoun, article, adjective, and numeral case forms. Tag each variant with separate case, number, gender, person, tense, mood, aspect, and degree tags as applicable."
        };
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
- Extract only vocabulary items that are actually written in {targetLanguage}.
- The candidate list is a hint list, not a command to include everything.
- Ignore words from {knownLanguage}, UI labels, buttons, menu text, metadata, and helper text.
- Ignore pronunciation notes and phonetic hints. Do not extract words from phrases such as ""pronunciation of"", ""pronounced"", ""sounds like"", ""say it like"", ""IPA"", or romanization notes.
- If a line pairs a word with a pronunciation hint, include only the real {targetLanguage} vocabulary word, never the pronunciation helper words.
- Preserve included candidate words exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities only when they are in {targetLanguage}.
- Include short/common words only when they are real {targetLanguage} vocabulary in the pasted material, not UI or helper text.
- Do not merge separate candidate words into phrases.
- Each key is one included candidate word in {targetLanguage}.
- Each value is an object with:
- ""translation"": the {knownLanguage} translation of the word
{exampleSentenceRule}
{exampleSentenceTranslationRule}
- ""example_sentence"" MUST be a complete {targetLanguage} sentence or natural phrase with at least two words. It must not be a definition or explanation.
- ""example_sentence_translation"" MUST translate the example sentence. It must not be copied into ""example_sentence"".

Format:
{{
""word1"": {{
    ""translation"": ""..."",
    ""example_sentence"": ""..."",
    ""example_sentence_translation"": ""...""
}}
}}

Possible candidate words:
{candidates}

Source sentences:
{sentences}

Input:
{input}";
    }

    private static IReadOnlyList<string> ExtractCandidateWords(string input)
    {
        return Regex.Matches(RemovePronunciationNotes(input), @"[\p{L}\p{M}]+(?:[''][\p{L}\p{M}]+)?")
            .Select(match => match.Value.Trim())
            .Where(word => !string.IsNullOrWhiteSpace(word) && !AppActionWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string RemovePronunciationNotes(string input)
    {
        var lines = Regex.Split(input, @"\r?\n");
        var cleanedLines = lines.Select(line =>
        {
            var markerIndex = FindFirstPronunciationMarker(line);
            return markerIndex < 0 ? line : line[..markerIndex];
        });

        return string.Join("\n", cleanedLines);
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
