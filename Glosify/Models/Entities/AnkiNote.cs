namespace Glosify.Models.Entities;

public class AnkiNote
{
    public Guid Id { get; set; }
    public Guid AnkiCollectionId { get; set; }
    public Guid QuizId { get; set; }
    public string ItemType { get; set; } = PracticeItemType.Words;
    public string? WordId { get; set; }
    public Guid? SentenceId { get; set; }
    public string TargetText { get; set; } = string.Empty;
    public string SourceText { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public AnkiCollection Collection { get; set; } = null!;
    public ICollection<AnkiCard> Cards { get; set; } = [];
}
