namespace Glosify.Models.Entities;

public class AnkiReview
{
    public Guid Id { get; set; }
    public Guid AnkiCollectionId { get; set; }
    public Guid AnkiCardId { get; set; }
    public Guid ClientToken { get; set; }
    public string Rating { get; set; } = string.Empty;
    public string PreviousState { get; set; } = string.Empty;
    public string NewState { get; set; } = string.Empty;
    public DateTimeOffset? PreviousDueAt { get; set; }
    public DateTimeOffset NewDueAt { get; set; }
    public int ScheduledDays { get; set; }
    public double ElapsedDays { get; set; }
    public double PreviousStability { get; set; }
    public double NewStability { get; set; }
    public double PreviousDifficulty { get; set; }
    public double NewDifficulty { get; set; }
    public double Retrievability { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public int? DurationMilliseconds { get; set; }
    public string SchedulerVersion { get; set; } = string.Empty;
    public DateTimeOffset ReviewedAt { get; set; }

    public AnkiCollection Collection { get; set; } = null!;
    public AnkiCard Card { get; set; } = null!;
}
