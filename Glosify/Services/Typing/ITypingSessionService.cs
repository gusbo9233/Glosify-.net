namespace Glosify.Services;

public interface ITypingSessionService
{
    TypingSessionData StartSession(
        string userId,
        Guid quizId,
        string quizName,
        string sourceLanguage,
        string targetLanguage,
        int wordCount,
        IReadOnlyList<TypingWordData> words);

    TypingSessionData? FindSession(string sessionId, string userId);
    void SaveSession(TypingSessionData session);
    TypingAnswerResult SubmitAnswer(TypingSessionData session, string userAnswer);
}

public record TypingSessionData
{
    public string SessionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public Guid QuizId { get; init; }
    public string QuizName { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public int WordCount { get; init; }
    public int CurrentIndex { get; set; }
    public int CorrectCount { get; set; }
    public int IncorrectCount { get; set; }
    public IReadOnlyList<TypingWordData> Words { get; init; } = [];
    public List<TypingWordData> IncorrectWords { get; init; } = [];
}

public record TypingAnswerResult
{
    public bool IsCorrect { get; init; }
    public string CorrectAnswer { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
    public TypingWordData? NextWord { get; init; }
}
