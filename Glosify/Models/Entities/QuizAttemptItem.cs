namespace Glosify.Models.Entities;

public class QuizAttemptItem
{
    public Guid Id { get; set; }
    public Guid QuizAttemptId { get; set; }
    // Snapshot identity, deliberately without a foreign key: history survives source edits/deletion.
    // Null on historical attempts recorded before item-level identity was available.
    public string? ItemId { get; set; }
    public bool IsSkipped { get; set; }
    public string Prompt { get; set; } = string.Empty;
    public string ExpectedAnswer { get; set; } = string.Empty;
    public string? GivenAnswer { get; set; }
    public bool IsCorrect { get; set; }
    public int Sequence { get; set; }
}
