namespace Glosify.Services.Learning;

public sealed record LearningItem(Guid QuizId, string QuizName, string SourceLanguage, string TargetLanguage,
    string ItemType, string ItemId, string Text, string Translation, int Mistakes, int Observations,
    DateTimeOffset LastMistakeAt, int QuizMistakes, int AnkiLapses);
public sealed record LearningMistakes(IReadOnlyList<LearningItem> Items, int TotalCount, int? NextOffset,
    int UnidentifiedAttemptItems, DateTimeOffset Since, int SummaryOnlyQuizAttempts = 0);
public sealed record LearningProgress(DateTimeOffset Since, int QuizAttempts, int Correct, int Incorrect, int Skipped,
    double? QuizAccuracyPercent, int AnkiReviews, int AnkiAgain, double? AnkiRetentionPercent,
    int UnidentifiedAttemptItems, int SummaryOnlyQuizAttempts = 0);
public interface ILearningInsightsService
{
    Task<LearningMistakes> MistakesAsync(string userId, string targetLanguage, Guid? quizId, string? itemType,
        int days, int offset, int limit, CancellationToken cancellationToken = default);
    Task<LearningProgress> ProgressAsync(string userId, string targetLanguage, Guid? quizId, int days, CancellationToken cancellationToken = default);
}
