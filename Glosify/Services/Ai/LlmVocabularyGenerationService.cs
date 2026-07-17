using System.Text.Json;
using Glosify.Services.Ai.Generation;

namespace Glosify.Services.Ai;

public sealed class LlmVocabularyGenerationService : IVocabularyGenerationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IGenerativeAiClient _generativeAi;

    public LlmVocabularyGenerationService(IGenerativeAiClient generativeAi)
    {
        _generativeAi = generativeAi;
    }

    public async Task<RepairWordResult?> RepairWordAsync(
        RepairQuizData quizData,
        string wordId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildRepairWordPrompt(quizData, wordId);
        var result = await _generativeAi.GenerateStructuredAsync<RepairWordResult>(
            prompt,
            new AiUsageContext(
                userId,
                AiUsageFeatures.Repair,
                "repair_word",
                Guid.NewGuid(),
                "word",
                wordId),
            cancellationToken: cancellationToken);
        return IsValidWordResult(result, wordId, quizData.Quiz.Id) ? result : null;
    }

    public async Task<RepairSentenceResult?> RepairSentenceAsync(
        RepairQuizData quizData,
        string sentenceText,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var prompt = BuildRepairSentencePrompt(quizData, sentenceText);
        var result = await _generativeAi.GenerateStructuredAsync<RepairSentenceResult>(
            prompt,
            new AiUsageContext(
                userId,
                AiUsageFeatures.Repair,
                "repair_sentence",
                Guid.NewGuid(),
                "quiz",
                quizData.Quiz.Id),
            cancellationToken: cancellationToken);
        return IsValidSentenceResult(result, quizData.Quiz.Id) ? result : null;
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

    private static bool IsValidWordResult(
        RepairWordResult? result,
        string wordId,
        string quizId) =>
        result?.Word is not null
        && string.Equals(result.Word.Id, wordId, StringComparison.Ordinal)
        && string.Equals(result.Word.QuizId, quizId, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(result.Word.Word)
        && !string.IsNullOrWhiteSpace(result.Word.Translation);

    private static bool IsValidSentenceResult(
        RepairSentenceResult? result,
        string quizId) =>
        result?.Sentence is not null
        && string.Equals(result.Sentence.QuizId, quizId, StringComparison.OrdinalIgnoreCase)
        && !string.IsNullOrWhiteSpace(result.Sentence.Text)
        && !string.IsNullOrWhiteSpace(result.Sentence.Translation);

}
