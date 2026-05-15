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
    private const string SystemInstruction = """
You are a computational linguistics expert generating structured language-learning data.
You are careful with messy learner notes: distinguish the actual target-language material from UI labels, translations, pronunciation aids, grammar labels, gender markers, language codes, and formatting artifacts.
Return only clean, learner-facing JSON. Never include markdown, explanations outside JSON, or note artifacts in vocabulary items.
""";

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
        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var json = await GenerateWordsWithAssistant(cleanedInput, knownLanguage, targetLanguage);
        if (!ValidateResponse(json))
        {
            json = await RepairJsonWithAssistant(
                json,
                BuildWordExtractionRepairPrompt(knownLanguage, targetLanguage),
                maxOutputTokens: 4_000);
        }

        var generatedWords = NormalizeGeneratedWords(json, cleanedInput);
        if (generatedWords.Count == 0)
        {
            _logger.LogWarning("Gemini word-extraction response failed validation for {KnownLanguage}->{TargetLanguage}", knownLanguage, targetLanguage);
            throw new InvalidOperationException("The AI assistant returned an unexpected response format.");
        }

        return generatedWords;
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
            json = await RepairJsonWithAssistant(
                json,
                BuildWordDetailRepairPrompt(knownLanguage, targetLanguage),
                maxOutputTokens: 8_000);
        }

        if (!ValidateWordDetailResponse(json))
        {
            var salvaged = SalvageWordDetail(json);
            if (salvaged is not null)
            {
                return salvaged;
            }

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
        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var cleanedSourceSentences = VocabularyInputCleaner.CleanSourceSentences(sourceSentences);
        var json = await GenerateWordsWithAssistant(
            cleanedInput,
            knownLanguage,
            targetLanguage,
            cleanedSourceSentences);
        if (!ValidateResponse(json))
        {
            json = await RepairJsonWithAssistant(
                json,
                BuildWordExtractionRepairPrompt(knownLanguage, targetLanguage),
                maxOutputTokens: 4_000);
        }

        var generatedWords = NormalizeGeneratedWords(json, cleanedInput);
        if (generatedWords.Count == 0)
        {
            _logger.LogWarning("Gemini word-extraction response failed validation for {KnownLanguage}->{TargetLanguage} with {SentenceCount} source sentences", knownLanguage, targetLanguage, sourceSentences.Count);
            throw new InvalidOperationException("The AI assistant returned an unexpected response format.");
        }

        return generatedWords;
    }

    public bool ValidateResponse(string json)
    {
        return TryReadGeneratedWords(json, out var words) && words.Count > 0;
    }

    private async Task<string> GenerateWordsWithAssistant(string input, string knownLanguage, string targetLanguage)
    {
        EnsureConfigured();

        var prompt = BuildWordExtractionPrompt(input, knownLanguage, targetLanguage);
        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: CreateJsonConfig(4_000)
        );

        return response.Candidates?[0].Content?.Parts?[0].Text ?? string.Empty;
    }

    private async Task<string> RepairJsonWithAssistant(
        string brokenJson,
        string schemaPrompt,
        int maxOutputTokens)
    {
        EnsureConfigured();

        var prompt = $@"
The previous response was not valid for the required JSON schema.
Repair it into valid JSON only. Do not add markdown, commentary, or text outside JSON.
Preserve all usable data from the previous response, but remove malformed fields and learner-note artifacts when needed.

Expected schema:
{schemaPrompt}

Previous response:
{brokenJson}";

        var client = new Client(apiKey: _apiKey);
        var response = await client.Models.GenerateContentAsync(
            model: _model,
            contents: prompt,
            config: CreateJsonConfig(maxOutputTokens)
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
            config: CreateJsonConfig(8_000)
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
            config: CreateJsonConfig(4_000)
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

    private static GenerateContentConfig CreateJsonConfig(int maxOutputTokens)
    {
        return new GenerateContentConfig
        {
            SystemInstruction = new Content
            {
                Parts = [new Part { Text = SystemInstruction }]
            },
            ResponseMimeType = "application/json",
            Temperature = 0.2f,
            MaxOutputTokens = maxOutputTokens
        };
    }

    private string BuildWordExtractionPrompt(string input, string knownLanguage, string targetLanguage)
    {
        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var candidateWords = ExtractCandidateWords(cleanedInput);
        var candidates = string.Join("\n", candidateWords.Select(word => $"- {word}"));
        var extractedSourceSentences = ExtractSourceSentences(cleanedInput);
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
- Use the TOKENS list as the authoritative list of possible surface forms. Do not invent words outside TOKENS.
- Keep keys as surface tokens only. Do not convert them to dictionary/headword/base forms during extraction.
- Treat the input as messy learner notes. First identify the clean {targetLanguage} text, then extract vocabulary from that clean text only.
- Ignore anything that functions as annotation, UI text, metadata, labels, grammar notes, gender notes, language names/codes, translations, pronunciation help, romanization, phonetic spelling, formatting symbols, or copy/paste artifacts.
- Do not treat glossary lines that combine {knownLanguage} translation, {targetLanguage} word, and pronunciation hint as example sentences.
- Do not copy wrappers or note markers into output. Words and example sentences must be clean learner-facing {targetLanguage}, without parentheses, brackets, slash labels, gender labels, language labels, or helper symbols unless the symbol is genuinely part of the target-language spelling.
- If a token could be either real {targetLanguage} vocabulary or surrounding annotation, include it only when the surrounding text clearly uses it as {targetLanguage}.
- Preserve included candidate words exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities only when they are in {targetLanguage}.
- Include short/common words only when they are real {targetLanguage} vocabulary in the pasted material, not UI or helper text.
- Do not merge separate candidate words into phrases.
- Each key is one included token in {targetLanguage}, copied exactly from TOKENS.
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

TOKENS:
{candidates}

Source sentences:
{sourceSentences}

Input:
{cleanedInput}";
    }

    private static string BuildWordDetailPrompt(
        string word,
        string translation,
        string knownLanguage,
        string targetLanguage)
    {
        var grammarGuidance = BuildLanguageSpecificGrammarGuidance(targetLanguage);

        return $@"
The user knows {knownLanguage} and is learning {targetLanguage}.
Create grammatical word detail for this vocabulary item and return a JSON object.

Vocabulary:
- word: {word}
- {knownLanguage} translation: {translation}

Rules:
- Output MUST be valid JSON only. No explanations, no extra text.
- Generate this from your language knowledge. Do not assume a dictionary lookup has already happened.
- Treat the given word as the learner's surface vocabulary item. Include its base/dictionary form in variants or properties only when helpful; do not replace the requested word.
- Do not invent rare or archaic forms unless they are central to normal use.
- Keep values concise and learner-friendly.
- ""properties"" MUST be an object. Include ""pos"" using one of: noun, verb, article, adjective, pronoun, adverb, preposition, conjunction, numeral, interjection.
- Add useful grammatical properties when relevant, such as gender, number, case, tense, aspect, mood, person, comparative, superlative, tags.
- ""variants"" MUST be an array of objects with ""form"" and ""tags"".
- Variant tags MUST be separate lowercase grammar tags, not combined labels. Use tags such as nominative, genitive, dative, accusative, singular, plural, infinitive, present, past, first-person, second-person, third-person, indicative, imperative, participle.
- Include the canonical form itself as a variant when it has meaningful tags.
- MUST include the requested surface word itself as a variant when variants are useful.
- For inflected nouns, verbs, adjectives, pronouns, articles, and numerals, include the common learner forms you are confident are correct. Use an empty array only if no variants are genuinely useful.

Language-specific grammar rules for {targetLanguage}:
{grammarGuidance}

- ""explanation"" MUST be a concise {knownLanguage} explanation of the word and its usage.
- ""example_sentence"" MUST be a natural {targetLanguage} sentence using the word, with at least two words.
- ""example_sentence_translation"" MUST be the {knownLanguage} translation of ""example_sentence"".
- ""example_sentence"" MUST NOT be the explanation, translation, a definition, or a one-word answer.
- ""explanation"", ""example_sentence"", and ""example_sentence_translation"" MUST be three different strings with distinct roles.
- Do not include learner-note wrappers or artifacts such as parentheses, brackets, slash labels, gender labels, language labels, pronunciation notes, or romanization unless they are genuinely part of the target-language spelling.

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
  ""example_sentence"": ""..."",
  ""example_sentence_translation"": ""...""
}}";
    }

    private static string BuildWordExtractionRepairPrompt(string knownLanguage, string targetLanguage)
    {
        return $@"
A JSON object whose keys are clean {targetLanguage} surface tokens and whose values have:
{{
  ""word"": {{
    ""translation"": ""clean {knownLanguage} translation"",
    ""example_sentence"": ""clean {targetLanguage} sentence or phrase"",
    ""example_sentence_translation"": ""clean {knownLanguage} translation of the example""
  }}
}}
No parentheses, brackets, slash labels, pronunciation notes, markdown, or prose outside JSON.";
    }

    private static string BuildWordDetailRepairPrompt(string knownLanguage, string targetLanguage)
    {
        return $@"
A JSON object with exactly these top-level fields:
{{
  ""properties"": {{ ""pos"": ""noun|verb|article|adjective|pronoun|adverb|preposition|conjunction|numeral|interjection"" }},
  ""variants"": [
    {{ ""form"": ""clean {targetLanguage} form"", ""tags"": [""lowercase grammar tag""] }}
  ],
  ""explanation"": ""concise {knownLanguage} explanation"",
  ""example_sentence"": ""clean natural {targetLanguage} sentence"",
  ""example_sentence_translation"": ""clean {knownLanguage} translation of the example sentence""
}}
No parentheses, brackets, slash labels, pronunciation notes, markdown, or prose outside JSON.";
    }

    private static string BuildLanguageSpecificGrammarGuidance(string targetLanguage)
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
- Polish verbs: include infinitive and aspect tags imperfective or perfective. For present or future person forms, include the tag non-past plus person and number. For past forms, always include person, number, and gender/group-gender tags: first-person/second-person/third-person, singular/plural, masculine/feminine/neuter for singular, and masculine-personal or non-masculine-personal for plural. For example, Polish ""they were"" needs separate third-person plural masculine-personal and third-person plural non-masculine-personal forms.",

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
        var cleanedInput = VocabularyInputCleaner.CleanForVocabulary(input);
        var cleanedSourceSentences = VocabularyInputCleaner.CleanSourceSentences(sourceSentences);
        var candidateWords = ExtractCandidateWords(cleanedInput);
        var candidates = string.Join("\n", candidateWords.Select(word => $"- {word}"));
        var sentences = string.Join("\n", cleanedSourceSentences.Select(sentence => $"- {sentence}"));
        var useSourceSentences = ShouldUseSourceSentences(cleanedSourceSentences);
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
- Use the TOKENS list as the authoritative list of possible surface forms. Do not invent words outside TOKENS.
- Keep keys as surface tokens only. Do not convert them to dictionary/headword/base forms during extraction.
- Treat the input as messy learner notes. First identify the clean {targetLanguage} text, then extract vocabulary from that clean text only.
- Ignore anything that functions as annotation, UI text, metadata, labels, grammar notes, gender notes, language names/codes, translations, pronunciation help, romanization, phonetic spelling, formatting symbols, or copy/paste artifacts.
- Do not treat glossary lines that combine {knownLanguage} translation, {targetLanguage} word, and pronunciation hint as example sentences.
- Do not copy wrappers or note markers into output. Words and example sentences must be clean learner-facing {targetLanguage}, without parentheses, brackets, slash labels, gender labels, language labels, or helper symbols unless the symbol is genuinely part of the target-language spelling.
- If a token could be either real {targetLanguage} vocabulary or surrounding annotation, include it only when the surrounding text clearly uses it as {targetLanguage}.
- Preserve included candidate words exactly as written, including inflected forms.
- Include proper nouns for places, countries, languages, and nationalities only when they are in {targetLanguage}.
- Include short/common words only when they are real {targetLanguage} vocabulary in the pasted material, not UI or helper text.
- Do not merge separate candidate words into phrases.
- Each key is one included token in {targetLanguage}, copied exactly from TOKENS.
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

TOKENS:
{candidates}

Source sentences:
{sentences}

Input:
{cleanedInput}";
    }

    private static IReadOnlyList<string> ExtractCandidateWords(string input)
    {
        return Regex.Matches(VocabularyInputCleaner.CleanForVocabulary(input), @"[\p{L}\p{M}]+(?:[''][\p{L}\p{M}]+)?")
            .Select(match => match.Value.Trim())
            .Where(word => !string.IsNullOrWhiteSpace(word) && !AppActionWords.Contains(word))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static IReadOnlyList<string> ExtractSourceSentences(string input)
    {
        return Regex.Split(VocabularyInputCleaner.CleanForVocabulary(input), @"(?<=[.!?])\s+|\r?\n+")
            .Select(sentence => sentence.Trim())
            .Where(sentence => !string.IsNullOrWhiteSpace(sentence))
            .ToList();
    }

    private static IReadOnlyDictionary<string, GeneratedWord> NormalizeGeneratedWords(string json, string input)
    {
        if (!TryReadGeneratedWords(json, out var generated))
        {
            generated = SalvageGeneratedWords(json);
        }

        var tokens = new HashSet<string>(ExtractCandidateWords(input), StringComparer.OrdinalIgnoreCase);
        var normalized = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);

        foreach (var (word, generatedWord) in generated)
        {
            var trimmedWord = word.Trim();
            var cleanTranslation = CleanGeneratedText(generatedWord.Translation);
            var cleanExampleSentence = CleanGeneratedExample(generatedWord.ExampleSentence);
            var cleanExampleTranslation = CleanGeneratedText(generatedWord.ExampleSentenceTranslation);

            if (!tokens.Contains(trimmedWord) || !IsCleanVocabularyKey(trimmedWord) || string.IsNullOrWhiteSpace(cleanTranslation))
            {
                continue;
            }

            normalized[trimmedWord] = new GeneratedWord
            {
                Translation = cleanTranslation,
                ExampleSentence = cleanExampleSentence,
                ExampleSentenceTranslation = cleanExampleTranslation
            };
        }

        return normalized;
    }

    private static Dictionary<string, GeneratedWord> SalvageGeneratedWords(string text)
    {
        var generated = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in Regex.Matches(
            text,
            @"""(?<word>[^""]+)""\s*:\s*\{(?<body>.*?)(?:\}\s*,|\}\s*\}|$)",
            RegexOptions.Singleline))
        {
            var word = UnescapeJsonString(match.Groups["word"].Value);
            var body = match.Groups["body"].Value;
            var translation = ReadJsonLikeString(body, "translation");
            if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            {
                continue;
            }

            generated[word] = new GeneratedWord
            {
                Translation = translation,
                ExampleSentence = ReadJsonLikeString(body, "example_sentence", "exampleSentence", "sentence"),
                ExampleSentenceTranslation = ReadJsonLikeString(
                    body,
                    "example_sentence_translation",
                    "exampleSentenceTranslation",
                    "sentence_translation",
                    "explanation")
            };
        }

        foreach (Match match in Regex.Matches(
            text,
            @"\{(?<body>[^{}]*(?:""lemma""|""word""|""token"")[^{}]*?""translation""[^{}]*?)\}",
            RegexOptions.Singleline))
        {
            var body = match.Groups["body"].Value;
            var word = ReadJsonLikeString(body, "lemma", "word", "token", "text");
            var translation = ReadJsonLikeString(body, "translation");
            if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            {
                continue;
            }

            generated[word] = new GeneratedWord
            {
                Translation = translation,
                ExampleSentence = ReadJsonLikeString(body, "example_sentence", "exampleSentence", "sentence"),
                ExampleSentenceTranslation = ReadJsonLikeString(
                    body,
                    "example_sentence_translation",
                    "exampleSentenceTranslation",
                    "sentence_translation",
                    "explanation")
            };
        }

        return generated;
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
                "sentence_translation",
                "explanation")
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

    private static string? ReadJsonLikeString(string text, params string[] names)
    {
        foreach (var name in names)
        {
            var match = Regex.Match(
                text,
                $@"""{Regex.Escape(name)}""\s*:\s*""(?<value>(?:\\.|[^""\\])*)""",
                RegexOptions.Singleline);
            if (match.Success)
            {
                return UnescapeJsonString(match.Groups["value"].Value);
            }
        }

        return null;
    }

    private static string UnescapeJsonString(string value)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($@"""{value}""") ?? value;
        }
        catch (JsonException)
        {
            return Regex.Unescape(value);
        }
    }

    private static GeneratedWordDetail? SalvageWordDetail(string text)
    {
        var explanation = ReadJsonLikeString(text, "explanation");
        var exampleSentence = ReadJsonLikeString(text, "example_sentence", "exampleSentence");
        var exampleSentenceTranslation = ReadJsonLikeString(
            text,
            "example_sentence_translation",
            "exampleSentenceTranslation",
            "sentence_translation");
        var pos = ReadJsonLikeString(text, "pos", "part_of_speech", "partOfSpeech");
        var properties = new Dictionary<string, JsonElement>();

        if (!string.IsNullOrWhiteSpace(pos))
        {
            properties["pos"] = JsonDocument.Parse(JsonSerializer.Serialize(pos)).RootElement.Clone();
        }

        var variants = new List<GeneratedWordVariant>();
        foreach (Match match in Regex.Matches(text, @"\{(?<body>[^{}]*""form""[^{}]*""tags""[^{}]*?)\}", RegexOptions.Singleline))
        {
            var body = match.Groups["body"].Value;
            var form = ReadJsonLikeString(body, "form");
            if (string.IsNullOrWhiteSpace(form))
            {
                continue;
            }

            var tags = Regex.Matches(body, @"""(?<tag>[a-z][a-z -]*)""", RegexOptions.IgnoreCase)
                .Select(tag => tag.Groups["tag"].Value)
                .Where(tag => !string.Equals(tag, "form", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(tag, "tags", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            variants.Add(new GeneratedWordVariant { Form = form, Tags = tags });
        }

        if (properties.Count == 0
            && variants.Count == 0
            && string.IsNullOrWhiteSpace(explanation)
            && string.IsNullOrWhiteSpace(exampleSentence))
        {
            return null;
        }

        return new GeneratedWordDetail
        {
            Properties = properties,
            Variants = variants,
            Explanation = CleanGeneratedText(explanation),
            ExampleSentence = CleanGeneratedExample(exampleSentence),
            ExampleSentenceTranslation = CleanGeneratedText(exampleSentenceTranslation)
        };
    }

    private static bool IsCleanVocabularyKey(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Regex.IsMatch(value, @"[0-9()[\]{}\\/|]");
    }

    private static bool IsCleanTranslation(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Regex.IsMatch(value, @"[()[\]{}\\/|]");
    }

    private static bool IsCleanExampleSentence(string? value)
    {
        return !string.IsNullOrWhiteSpace(value)
            && !Regex.IsMatch(value, @"[()[\]{}\\/|]");
    }

    private static string CleanGeneratedText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var cleaned = Regex.Replace(value, @"\s*[\(\[\{][^\)\]\}]*[\)\]\}]\s*", " ");
        cleaned = Regex.Replace(cleaned, @"[/\\|]+", ", ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', ',', '.', ';', ':');
        return cleaned;
    }

    private static string CleanGeneratedExample(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return VocabularyInputCleaner.CleanForVocabulary(value);
    }

    private static bool ShouldUseSourceSentences(IReadOnlyList<string> sourceSentences)
    {
        if (sourceSentences.Count == 0)
        {
            return false;
        }

        return sourceSentences.Any(sentence =>
            ExtractCandidateWords(sentence).Count > 2
            && Regex.IsMatch(sentence.Trim(), @"[.!?]$"));
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
            if (!root.TryGetProperty("example_sentence_translation", out var exampleTranslation) || exampleTranslation.ValueKind != JsonValueKind.String) return false;

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
