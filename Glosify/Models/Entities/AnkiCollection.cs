namespace Glosify.Models.Entities;

// One collection per user and learning language. Notes carry their own source
// language, so quizzes translated from different languages share one queue.
public class AnkiCollection
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string DefaultDirection { get; set; } = PracticeDirection.SourceToTarget;
    // Retained for existing database rows and assistant Undo journals. Scheduling
    // ignores these legacy daily limits, including existing stored values.
    public double DesiredRetention { get; set; } = AnkiStudyDefaults.DesiredRetention;
    public int NewCardsPerDay { get; set; } = int.MaxValue;
    public int MaximumReviewsPerDay { get; set; } = int.MaxValue;
    public string TimeZoneId { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AnkiQuizLink> QuizLinks { get; set; } = [];
    public ICollection<AnkiNote> Notes { get; set; } = [];
    public ICollection<AnkiReview> Reviews { get; set; } = [];
}
