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
    // uses AnkiStudyDefaults, including for collections with old custom values.
    public double DesiredRetention { get; set; } = AnkiStudyDefaults.DesiredRetention;
    public int NewCardsPerDay { get; set; } = AnkiStudyDefaults.NewCardsPerDay;
    public int MaximumReviewsPerDay { get; set; } = AnkiStudyDefaults.MaximumReviewsPerDay;
    public string TimeZoneId { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AnkiQuizLink> QuizLinks { get; set; } = [];
    public ICollection<AnkiNote> Notes { get; set; } = [];
    public ICollection<AnkiReview> Reviews { get; set; } = [];
}
