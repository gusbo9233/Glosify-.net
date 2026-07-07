namespace Glosify.Services.Flashcards;

public interface IFlashcardSessionService
{
    FlashcardSessionData StartSession(string userId, Guid quizId, string quizName, string sourceLanguage, string targetLanguage, int wordCount, IReadOnlyList<FlashcardCardData> cards, string? practiceDirection = null, string? practiceItemType = null);
    FlashcardSessionData? FindSession(string sessionId, string userId);
    FlashcardSessionData? FindResumableSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount);
    void ResetSession(string userId, Guid quizId, string? practiceDirection, string? practiceItemType, int wordCount);
    void SaveSession(FlashcardSessionData session);
    void ApplyRating(FlashcardSessionData session, string rating);
    void RevealAnswer(FlashcardSessionData session);
}

public record FlashcardSessionData : Glosify.Services.Quizzes.IQuizSessionData
{
    public string SessionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public Guid QuizId { get; init; }
    public string QuizName { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public string PracticeDirection { get; init; } = Glosify.Models.PracticeDirection.SourceToTarget;
    public string PromptLanguage { get; init; } = string.Empty;
    public string AnswerLanguage { get; init; } = string.Empty;
    public string PracticeItemType { get; init; } = Glosify.Models.PracticeItemType.Words;
    public int WordCount { get; init; }
    public int CurrentIndex { get; set; }
    public int RememberedCount { get; set; }
    public int AgainCount { get; set; }
    public int SkippedCount { get; set; }
    public bool IsAnswerRevealed { get; set; }
    public Guid? ClassroomId { get; set; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.UtcNow;
    public bool AttemptRecorded { get; set; }
    public IReadOnlyList<FlashcardCardData> Cards { get; init; } = [];
    public List<FlashcardCardData> AgainCards { get; init; } = [];

}

public record FlashcardCardData
{
    public string Id { get; init; } = string.Empty;
    public string Lemma { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public string Prompt { get; init; } = string.Empty;
    public string Answer { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
}
