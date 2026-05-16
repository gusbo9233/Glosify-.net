namespace Glosify.Services;

public interface IGeneratedVocabularyService
{
    Task<GeneratedVocabularyResult> GenerateAndAddWordsAsync(Guid quizId, string userId, string input, string? aiProvider = null);
}

public sealed record GeneratedVocabularyResult(int AddedCount, string? Error, string? Message)
{
    public static GeneratedVocabularyResult Failure(string error) => new(0, error, null);
    public static GeneratedVocabularyResult Success(int added, string message) => new(added, null, message);
}
