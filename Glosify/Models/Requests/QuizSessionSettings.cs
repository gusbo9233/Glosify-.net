namespace Glosify.Models.Requests;

public class QuizSessionSettings
{
    public Guid? QuizId { get; set; }
    public int WordCount { get; set; }
    public string Mode { get; set; } = "flashcards";
    public string QuizType
    {
        get => Mode;
        set => Mode = value;
    }
    public string? Language { get; set; }
    public int? Difficulty { get; set; }
}
