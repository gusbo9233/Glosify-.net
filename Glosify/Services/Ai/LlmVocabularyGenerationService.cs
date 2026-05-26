using System.Text.Json;
using System.Text.Json.Serialization;

namespace Glosify.Services;

public sealed class LlmVocabularyGenerationService : IVocabularyGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGeminiClient _gemini;
    private readonly ILogger<LlmVocabularyGenerationService> _logger;

    public LlmVocabularyGenerationService(
        IGeminiClient gemini,
        ILogger<LlmVocabularyGenerationService> logger)
    {
        _gemini = gemini;
        _logger = logger;
    }

    public async Task<IReadOnlyDictionary<string, GeneratedWord>> GenerateWordsFromTextAsync(
        string input,
        string sourceLanguage,
        string targetLanguage,
        string? quizName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            throw new InvalidOperationException("No input text was provided for vocabulary generation.");
        }

        var prompt = BuildBulkVocabularyPrompt(input, sourceLanguage, targetLanguage, quizName);
        var responseText = await _gemini.GenerateJsonAsync(prompt, cancellationToken: cancellationToken);
        var response = TryDeserialize<LlmVocabularyResponse>(responseText);
        if (response?.Words is not { Count: > 0 })
        {
            _logger.LogWarning(
                "Gemini bulk vocabulary generation returned no usable words. Raw response length {Length}.",
                responseText.Length);
            throw new InvalidOperationException(
                $"The assistant could not find {targetLanguage} vocabulary in the submitted text.");
        }

        var sentences = response.Sentences ?? [];
        var normalized = new Dictionary<string, GeneratedWord>(StringComparer.OrdinalIgnoreCase);
        foreach (var word in response.Words)
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
                ExampleSentence = CleanText(word.ExampleSentence) is { Length: > 0 } exampleSentence
                    ? exampleSentence
                    : CleanText(sentence?.Text),
                ExampleSentenceTranslation = CleanText(word.ExampleSentenceTranslation) is { Length: > 0 } exampleTranslation
                    ? exampleTranslation
                    : CleanText(sentence?.Translation),
                ExampleSentenceWord = CleanText(word.ExampleSentenceWord),
            };
        }

        return normalized;
    }

    public async Task<GeneratedWordDetail?> GenerateWordDetailAsync(
        string word,
        string translation,
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        var prompt = BuildWordDetailPrompt(word, translation, sourceLanguage, targetLanguage);
        var responseText = await _gemini.GenerateJsonAsync(prompt, cancellationToken: cancellationToken);
        var response = TryDeserialize<LlmWordDetailResponse>(responseText);
        if (response == null)
        {
            _logger.LogWarning("Gemini word-detail generation returned an unreadable response for {Word}.", word);
            return null;
        }

        return new GeneratedWordDetail
        {
            Properties = NormalizeProperties(response.Properties),
            Variants = NormalizeVariants(response.Variants),
            Explanation = CleanText(response.Explanation),
            ExampleSentence = CleanText(response.ExampleSentence),
            ExampleSentenceTranslation = CleanText(response.ExampleSentenceTranslation),
        };
    }

    public async Task<RepairQuizResult?> RepairQuizAsync(
        RepairQuizData quizData,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildRepairQuizPrompt(quizData);
        var responseText = await _gemini.GenerateJsonAsync(prompt, cancellationToken: cancellationToken);
        var repaired = TryDeserialize<RepairQuizData>(responseText);
        if (repaired == null)
        {
            _logger.LogWarning("Gemini quiz repair returned an unreadable response.");
            return null;
        }

        return new RepairQuizResult { QuizData = repaired };
    }

    public async Task<RepairWordResult?> RepairWordAsync(
        RepairQuizData quizData,
        string wordId,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildRepairWordPrompt(quizData, wordId);
        var responseText = await _gemini.GenerateJsonAsync(prompt, cancellationToken: cancellationToken);
        return TryDeserialize<RepairWordResult>(responseText);
    }

    public async Task<RepairSentenceResult?> RepairSentenceAsync(
        RepairQuizData quizData,
        string sentenceText,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildRepairSentencePrompt(quizData, sentenceText);
        var responseText = await _gemini.GenerateJsonAsync(prompt, cancellationToken: cancellationToken);
        return TryDeserialize<RepairSentenceResult>(responseText);
    }

    private static string BuildBulkVocabularyPrompt(string input, string sourceLanguage, string targetLanguage, string? quizName)
    {
        var nameHint = string.IsNullOrWhiteSpace(quizName)
            ? string.Empty
            : $" The quiz is titled \"{quizName.Trim()}\".";
        return $$"""
        You are a language-learning assistant. Extract vocabulary from {{targetLanguage}} text for a learner whose first language is {{sourceLanguage}}.{{nameHint}}

        Rules:
        - The "lemma" is the dictionary form in {{targetLanguage}}.
        - The "translation" is a concise {{sourceLanguage}} gloss. Use the most natural meaning, not a pronunciation hint.
        - Skip closed-class words (articles, common prepositions, basic pronouns) unless they are central to the text.
        - For every word, include one natural full example sentence in {{targetLanguage}} that uses that lemma or a natural inflected form of it.
        - For every word, set "example_sentence_word" to the exact word form from the example sentence that corresponds to the lemma.
        - Prefer sentences taken from the input text when they are already grammatical and complete; otherwise write a short sentence inspired by the input.
        - Each example sentence must be useful to a learner: no pronunciation hints, gender notes, slash-separated alternatives, dictionary glosses, fragments, or markup.
        - Translate each example sentence into natural {{sourceLanguage}}.
        - Do not include grammatical notes, parts of speech, or markup in the lemma or translation.
        - Output strictly the JSON object described below. No commentary, no markdown fences.

        Output schema:
        {
          "words": [
            {
              "lemma": "string ({{targetLanguage}})",
              "translation": "string ({{sourceLanguage}})",
              "example_sentence": "string ({{targetLanguage}})",
              "example_sentence_translation": "string ({{sourceLanguage}})",
              "example_sentence_word": "string (the exact target-language word form used in example_sentence for this lemma)"
            }
          ],
          "sentences": [
            { "text": "string ({{targetLanguage}})", "translation": "string ({{sourceLanguage}})" }
          ]
        }

        Input text:
        ---
        {{input}}
        ---
        """;
    }

    private static string BuildWordDetailPrompt(string word, string translation, string sourceLanguage, string targetLanguage)
    {
        return $$"""
        You are a language-learning assistant. Build a detailed dictionary entry for a single {{targetLanguage}} word for a learner whose first language is {{sourceLanguage}}.

        Word ({{targetLanguage}}): {{word}}
        Translation ({{sourceLanguage}}): {{translation}}

        Rules:
        - "properties" is a flat map of grammatical info (e.g. "pos", "gender", "aspect", "transitivity"). Keys are lowercase snake_case. Values are short strings.
        - "variants" lists inflected forms (declensions, conjugations). Each has a "form" (the inflected word in {{targetLanguage}}), a learner-facing "label", an optional learner-facing "group", and optional "tags" metadata.
        - Do not invent empty paradigm slots. Return only forms that are useful and known for this word.
        - "explanation" is one or two sentences in {{sourceLanguage}} explaining nuance, register, or common collocations.
        - "example_sentence" is one natural full sentence in {{targetLanguage}} using the lemma or a natural inflected form in context.
        - "example_sentence" must not contain learner notes, pronunciation hints, slash-separated alternatives, dictionary glosses, fragments, or markup.
        - "example_sentence_translation" is a natural {{sourceLanguage}} translation of that sentence.
        - Output strictly the JSON object below. No commentary, no markdown fences.

        Output schema:
        {
          "properties": { "key": "value" },
          "variants": [{ "form": "string ({{targetLanguage}})", "label": "string", "group": "string", "tags": ["string"] }],
          "explanation": "string ({{sourceLanguage}})",
          "example_sentence": "string ({{targetLanguage}})",
          "example_sentence_translation": "string ({{sourceLanguage}})"
        }
        """;
    }

    private static string BuildRepairQuizPrompt(RepairQuizData quizData)
    {
        var payload = JsonSerializer.Serialize(quizData, JsonOptions);
        return $$"""
        You are a language-learning assistant repairing a quiz. The quiz teaches "{{quizData.Quiz.TargetLanguage}}" to speakers of "{{quizData.Quiz.SourceLanguage}}".

        Rules for the repaired output:
        - Keep every word's "id" stable. Do not invent or drop words.
        - Fix obvious lemma/translation typos. Lemmas stay in {{quizData.Quiz.TargetLanguage}}; translations stay in {{quizData.Quiz.SourceLanguage}}.
        - Ensure each example_sentence is a natural full sentence in {{quizData.Quiz.TargetLanguage}}, with a natural {{quizData.Quiz.SourceLanguage}} translation.
        - Example sentences must exercise the relevant quiz word using the lemma or a natural inflected form.
        - Remove learner notes, pronunciation hints, slash-separated alternatives, dictionary glosses, fragments, and markup from example_sentence fields.
        - Fill missing word_details (properties, variants, explanation) where they are empty. For variants, return only actual forms with their own display labels/groups; do not emit empty paradigm slots.
        - Preserve all "id" fields exactly as given. Use snake_case keys as in the input.
        - Output strictly a JSON object matching the same shape as the input. No commentary, no markdown fences.

        Input quiz data:
        {{payload}}
        """;
    }

    private static string BuildRepairWordPrompt(RepairQuizData quizData, string wordId)
    {
        var payload = JsonSerializer.Serialize(quizData, JsonOptions);
        return $$"""
        You are a language-learning assistant repairing a single word in a quiz. Target language: {{quizData.Quiz.TargetLanguage}}. Source language: {{quizData.Quiz.SourceLanguage}}.

        Word to repair: id = "{{wordId}}".

        Rules:
        - Keep the word's "id" exactly as given.
        - Fix lemma/translation typos. The lemma stays in {{quizData.Quiz.TargetLanguage}}; the translation stays in {{quizData.Quiz.SourceLanguage}}.
        - Rebuild the associated word_detail (properties, variants, explanation, example_sentence, example_sentence_translation) so it is complete and accurate. Reuse the existing word_detail "id" exactly.
        - The example_sentence must be a natural full {{quizData.Quiz.TargetLanguage}} sentence that uses this word's lemma or a natural inflected form.
        - Do not put learner notes, pronunciation hints, slash-separated alternatives, dictionary glosses, fragments, or markup in example_sentence.
        - Output strictly a JSON object with the shape below. No commentary, no markdown fences.

        Output schema:
        {
          "word": { "id": "{{wordId}}", "lemma": "string", "translation": "string", "word_detail_id": "string", "quiz_id": "string" },
          "word_detail": {
            "id": "string",
            "properties": { "key": "value" },
            "example_sentence": "string",
            "example_sentence_translation": "string",
            "explanation": "string",
            "variants": [{ "form": "string", "label": "string", "group": "string", "tags": ["string"] }],
            "language": "string"
          }
        }

        Input quiz data:
        {{payload}}
        """;
    }

    private static string BuildRepairSentencePrompt(RepairQuizData quizData, string sentenceText)
    {
        var payload = JsonSerializer.Serialize(quizData, JsonOptions);
        return $$"""
        You are a language-learning assistant repairing a single example sentence. Target language: {{quizData.Quiz.TargetLanguage}}. Source language: {{quizData.Quiz.SourceLanguage}}.

        Sentence to repair (current text in {{quizData.Quiz.TargetLanguage}}):
        ---
        {{sentenceText}}
        ---

        Rules:
        - Produce a natural, grammatical full sentence in {{quizData.Quiz.TargetLanguage}} that exercises one or more words from the quiz.
        - Preserve the learning purpose of the original sentence: keep the same quiz word(s) when possible, using natural inflected forms if needed.
        - Do not output learner notes, pronunciation hints, slash-separated alternatives, dictionary glosses, fragments, or markup.
        - Provide a natural {{quizData.Quiz.SourceLanguage}} translation of the sentence.
        - Output strictly the JSON object below. No commentary, no markdown fences.

        Output schema:
        {
          "sentence": {
            "id": "string",
            "text": "string ({{quizData.Quiz.TargetLanguage}})",
            "translation": "string ({{quizData.Quiz.SourceLanguage}})",
            "quiz_id": "{{quizData.Quiz.Id}}"
          }
        }

        Full quiz context:
        {{payload}}
        """;
    }

    private static T? TryDeserialize<T>(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return default;
        }

        var stripped = StripJsonFences(responseText);
        try
        {
            return JsonSerializer.Deserialize<T>(stripped, JsonOptions);
        }
        catch (JsonException)
        {
            return default;
        }
    }

    private static string StripJsonFences(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var newlineIndex = trimmed.IndexOf('\n');
            if (newlineIndex >= 0)
            {
                trimmed = trimmed[(newlineIndex + 1)..];
            }
            if (trimmed.EndsWith("```", StringComparison.Ordinal))
            {
                trimmed = trimmed[..^3];
            }
        }
        return trimmed.Trim();
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

    private static List<GeneratedWordVariant> NormalizeVariants(List<LlmVariant>? variants)
    {
        if (variants == null || variants.Count == 0)
        {
            return [];
        }

        return variants
            .Select(variant => new GeneratedWordVariant
            {
                Form = CleanText(variant.Form),
                Label = CleanText(variant.Label),
                Group = CleanText(variant.Group),
                Tags = variant.Tags?
                    .Select(CleanText)
                    .Where(tag => !string.IsNullOrWhiteSpace(tag))
                    .ToList() ?? [],
            })
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Form))
            .ToList();
    }

    private static LlmSentence? FindSentenceForWord(string lemma, List<LlmSentence> sentences)
    {
        return sentences.FirstOrDefault(sentence =>
            !string.IsNullOrWhiteSpace(sentence.Text)
            && sentence.Text.Contains(lemma, StringComparison.OrdinalIgnoreCase));
    }

    private static string CleanText(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private sealed class LlmVocabularyResponse
    {
        public List<LlmWord> Words { get; set; } = [];
        public List<LlmSentence> Sentences { get; set; } = [];
    }

    private sealed class LlmWord
    {
        public string Lemma { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        [JsonPropertyName("example_sentence")]
        public string ExampleSentence { get; set; } = string.Empty;
        [JsonPropertyName("example_sentence_translation")]
        public string ExampleSentenceTranslation { get; set; } = string.Empty;
        [JsonPropertyName("example_sentence_word")]
        public string ExampleSentenceWord { get; set; } = string.Empty;
    }

    private sealed class LlmSentence
    {
        public string Text { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
    }

    private sealed class LlmWordDetailResponse
    {
        public Dictionary<string, JsonElement> Properties { get; set; } = [];
        public List<LlmVariant> Variants { get; set; } = [];
        public string Explanation { get; set; } = string.Empty;
        [JsonPropertyName("example_sentence")]
        public string ExampleSentence { get; set; } = string.Empty;
        [JsonPropertyName("example_sentence_translation")]
        public string ExampleSentenceTranslation { get; set; } = string.Empty;
    }

    private sealed class LlmVariant
    {
        public string Form { get; set; } = string.Empty;
        public string Label { get; set; } = string.Empty;
        public string Group { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = [];
    }
}
