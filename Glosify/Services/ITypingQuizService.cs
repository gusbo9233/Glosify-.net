namespace Glosify.Services;

public interface ITypingQuizService
{
    Task<TypingQuizData> GetQuizDataAsync(Guid quizId, int wordCount);
    bool CheckAnswer(string userAnswer, string correctAnswer);
}

public record TypingQuizData
{
    public Guid QuizId { get; init; }
    public string QuizName { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public IReadOnlyList<TypingWordData> Words { get; init; } = [];
}

public record TypingWordData
{
    public string Id { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
}
