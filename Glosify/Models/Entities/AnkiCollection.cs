namespace Glosify.Models.Entities;

public class AnkiCollection
{
    public Guid Id { get; set; }
    public string UserId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string SourceLanguage { get; set; } = string.Empty;
    public string TargetLanguage { get; set; } = string.Empty;
    public string DefaultDirection { get; set; } = PracticeDirection.SourceToTarget;
    public double DesiredRetention { get; set; } = 0.9;
    public int NewCardsPerDay { get; set; } = 20;
    public int MaximumReviewsPerDay { get; set; } = 200;
    public string TimeZoneId { get; set; } = "UTC";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public ICollection<AnkiQuizLink> QuizLinks { get; set; } = [];
    public ICollection<AnkiNote> Notes { get; set; } = [];
    public ICollection<AnkiReview> Reviews { get; set; } = [];
}
