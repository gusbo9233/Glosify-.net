using Glosify.Models.QuizImports;

namespace Glosify.Services.Quizzes;

public interface IQuizJsonImportService
{
    Task<QuizJsonImportPreview> PreviewAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default);

    Task<QuizJsonImportResult> ApplyAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default);
}

public sealed class QuizJsonImportValidationException(
    IReadOnlyDictionary<string, string[]> errors,
    string? canonicalJson = null) : InvalidOperationException("The JSON import is invalid.")
{
    public IReadOnlyDictionary<string, string[]> Errors { get; } = errors;
    public string? CanonicalJson { get; } = canonicalJson;
}

public sealed class QuizJsonImportAiUnprocessableException(
    IReadOnlyDictionary<string, string[]>? errors = null,
    string? canonicalJson = null)
    : InvalidOperationException("The AI could not repair this import into a valid Glosify document.")
{
    public IReadOnlyDictionary<string, string[]>? Errors { get; } = errors;
    public string? CanonicalJson { get; } = canonicalJson;
}
