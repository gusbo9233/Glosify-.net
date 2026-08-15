namespace Glosify.Models.Entities;

public static class AnkiCardStates
{
    public const string New = "new";
    public const string Learning = "learning";
    public const string Review = "review";
    public const string Relearning = "relearning";
}

public class AnkiCard
{
    public Guid Id { get; set; }
    public Guid AnkiNoteId { get; set; }
    public string Direction { get; set; } = PracticeDirection.SourceToTarget;
    public string State { get; set; } = AnkiCardStates.New;
    public DateTimeOffset? DueAt { get; set; }
    public double Stability { get; set; }
    public double Difficulty { get; set; }
    public int LearningStep { get; set; }
    public int ReviewCount { get; set; }
    public int LapseCount { get; set; }
    public int ScheduledDays { get; set; }
    public DateTimeOffset? LastReviewedAt { get; set; }
    public DateTimeOffset? BuriedUntil { get; set; }
    public bool IsActive { get; set; } = true;
    public bool DirectlyIncluded { get; set; }
    public bool QuizLinkIncluded { get; set; }
    public bool ExcludedFromQuizLink { get; set; }
    public byte[] RowVersion { get; set; } = [];

    public AnkiNote Note { get; set; } = null!;
    public ICollection<AnkiReview> Reviews { get; set; } = [];
}
