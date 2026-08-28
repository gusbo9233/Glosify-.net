namespace Glosify.Services.Quizzes;

/// <summary>
/// Builds the copyable external-AI prompt shown by the quiz JSON import dialog.
/// </summary>
public static class QuizJsonGenerationPrompt
{
    public static string Build(
        bool isFreestyle,
        string displayLanguage,
        string importDestination) =>
        isFreestyle ? $$"""
            Create a Glosify version 1 quiz-import JSON document about the requested subject. The import will be placed in {{importDestination}}.

            Return JSON only. Use source_language "Freestyle". Each quiz must contain a words array whose word value is a prompt, question, or term and whose translation value is its answer or definition. Do not include sentences.

            Shape: { "version": 1, "source_language": "Freestyle", "quizzes": [{ "name": "Quiz name", "words": [{ "word": "Prompt", "translation": "Answer" }], "sentences": [] }], "collections": [] }

            Limits: 25 collections, five collection levels, 50 quizzes, 100 items per quiz, and 1,000 items total.
            """ : $$"""
            Create a Glosify version 1 quiz-import JSON document for a learner studying {{displayLanguage}}. The import will be placed in {{importDestination}}.

            Return JSON only: no markdown fence, prose, ids, visibility, target_language, or unsupported fields. Put words and sentence text in {{displayLanguage}} and translations in source_language. Each quiz needs at least one word or sentence. Use one root source_language and add a per-quiz source_language only when it differs.

            Shape:
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [
                {
                  "name": "Quiz name",
                  "words": [{ "word": "word or short phrase", "translation": "translation" }],
                  "sentences": [{ "text": "full sentence", "translation": "translation" }]
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

            Limits: 25 collections, five collection levels, 50 quizzes, 100 items per quiz, and 1,000 items total. Preserve the requested learning content and use natural translations.
            """;
}
