namespace Glosify.Models.Entities;

public class AnkiQuizLink
{
    public Guid Id { get; set; }
    public Guid AnkiCollectionId { get; set; }
    public Guid QuizId { get; set; }
    public bool WordsSourceToTarget { get; set; }
    public bool WordsTargetToSource { get; set; }
    public bool SentencesSourceToTarget { get; set; }
    public bool SentencesTargetToSource { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AnkiCollection Collection { get; set; } = null!;
    public Quiz Quiz { get; set; } = null!;
}
