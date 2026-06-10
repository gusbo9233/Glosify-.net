using System.Text.Json;

namespace Glosify.Services;

public sealed class LlmVocabularyGenerationService : IVocabularyGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGeminiClient _gemini;

    public LlmVocabularyGenerationService(IGeminiClient gemini)
    {
        _gemini = gemini;
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

    private static string BuildRepairWordPrompt(RepairQuizData quizData, string wordId)
    {
        var payload = JsonSerializer.Serialize(quizData, JsonOptions);
        return $$"""
        You are a language-learning assistant repairing a single word in a quiz. Target language: {{quizData.Quiz.TargetLanguage}}. Source language: {{quizData.Quiz.SourceLanguage}}.

        Word to repair: id = "{{wordId}}".

        Rules:
        - Keep the word's "id" exactly as given.
        - Fix word/translation typos. The word stays in {{quizData.Quiz.TargetLanguage}}; the translation stays in {{quizData.Quiz.SourceLanguage}}.
        - Output strictly a JSON object with the shape below. No commentary, no markdown fences.

        Output schema:
        {
          "word": { "id": "{{wordId}}", "word": "string", "translation": "string", "quiz_id": "string" }
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

}
