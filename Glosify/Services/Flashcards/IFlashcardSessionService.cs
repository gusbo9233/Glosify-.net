namespace Glosify.Services;

public interface IFlashcardSessionService
{
    FlashcardSessionData StartSession(string userId, Guid quizId, string quizName, string sourceLanguage, string targetLanguage, int wordCount, IReadOnlyList<FlashcardCardData> cards);
    FlashcardSessionData? FindSession(string sessionId, string userId);
    void SaveSession(FlashcardSessionData session);
    void ApplyRating(FlashcardSessionData session, string rating);
    void RevealAnswer(FlashcardSessionData session);
}

public record FlashcardSessionData
{
    public string SessionId { get; init; } = string.Empty;
    public string UserId { get; init; } = string.Empty;
    public Guid QuizId { get; init; }
    public string QuizName { get; init; } = string.Empty;
    public string SourceLanguage { get; init; } = string.Empty;
    public string TargetLanguage { get; init; } = string.Empty;
    public int WordCount { get; init; }
    public int CurrentIndex { get; set; }
    public int RememberedCount { get; set; }
    public int AgainCount { get; set; }
    public int SkippedCount { get; set; }
    public bool IsAnswerRevealed { get; set; }
    public IReadOnlyList<FlashcardCardData> Cards { get; init; } = [];
    public List<FlashcardCardData> AgainCards { get; init; } = [];

}

public record FlashcardCardData
{
    public string Id { get; init; } = string.Empty;
    public string Lemma { get; init; } = string.Empty;
    public string Translation { get; init; } = string.Empty;
    public string ExampleSentence { get; init; } = string.Empty;
    public string ExampleTranslation { get; init; } = string.Empty;
}
