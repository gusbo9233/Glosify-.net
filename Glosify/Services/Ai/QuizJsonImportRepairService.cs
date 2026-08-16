using System.Text.Json;
using Glosify.Models.QuizImports;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;

namespace Glosify.Services.Ai;

public sealed class QuizJsonImportRepairService : IQuizJsonImportRepairService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IQuizJsonImportService _imports;
    private readonly IGenerativeAiClient _generativeAi;

    public QuizJsonImportRepairService(
        IQuizJsonImportService imports,
        IGenerativeAiClient generativeAi)
    {
        _imports = imports;
        _generativeAi = generativeAi;
    }

    public async Task<QuizJsonImportPreview> RepairAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyDictionary<string, string[]> errors;
        try
        {
            return await _imports.PreviewAsync(
                json,
                targetLanguage,
                parentCollectionId,
                userId,
                cancellationToken);
        }
        catch (QuizJsonImportValidationException exception)
        {
            errors = exception.Errors;
        }

        var repaired = await _generativeAi.GenerateJsonAsync<QuizJsonImportAiRepairEnvelope>(
            BuildPrompt(json, targetLanguage, errors),
            new AiUsageContext(
                userId,
                AiUsageFeatures.JsonImportRepair,
                "repair_quiz_json_import",
                Guid.NewGuid(),
                "quiz_import"),
            cancellationToken: cancellationToken);
        if (string.IsNullOrWhiteSpace(repaired.RepairedJson))
        {
            throw new QuizJsonImportAiUnprocessableException();
        }

        try
        {
            return await _imports.PreviewAsync(
                repaired.RepairedJson,
                targetLanguage,
                parentCollectionId,
                userId,
                cancellationToken);
        }
        catch (QuizJsonImportValidationException)
        {
            throw new QuizJsonImportAiUnprocessableException();
        }
    }

    private static string BuildPrompt(
        string json,
        string targetLanguage,
        IReadOnlyDictionary<string, string[]> errors)
    {
        var errorJson = JsonSerializer.Serialize(errors, JsonOptions);
        return $$"""
        Repair the supplied text into a Glosify version 1 quiz-import JSON document.

        The target language is fixed by Glosify as {{targetLanguage}} and must not appear in the document. Preserve every usable collection name, quiz name, source language, word, sentence, and translation. Correct syntax and field structure only. Do not translate, improve, invent, remove, or merge learning content. Do not add ids, visibility, target_language, custom quizzes, or commentary.

        Required snake_case shape:
        {
          "version": 1,
          "source_language": "English",
          "quizzes": [
            {
              "name": "Quiz name",
              "source_language": "optional per-quiz override",
              "words": [{ "word": "target-language word", "translation": "source-language translation" }],
              "sentences": [{ "text": "target-language sentence", "translation": "source-language translation" }]
            }
          ],
          "collections": [
            {
              "name": "Collection name",
              "quizzes": [],
              "collections": []
            }
          ]
        }

        Return an object with exactly one property named repaired_json. Its value must be the complete repaired Glosify document encoded as a JSON string.

        Validation errors:
        {{errorJson}}

        Supplied text:
        ---
        {{json}}
        ---
        """;
    }
}
