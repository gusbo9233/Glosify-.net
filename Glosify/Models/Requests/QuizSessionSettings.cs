using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public class QuizSessionSettings
{
    public Guid? QuizId { get; set; }

    [Range(1, 200, ErrorMessage = "Pick a word count between 1 and 200.")]
    public int WordCount { get; set; }

    [Required]
    [StringLength(32)]
    public string Mode { get; set; } = "flashcards";

    public string QuizType
    {
        get => Mode;
        set => Mode = value;
    }

    public string? Language { get; set; }
    public int? Difficulty { get; set; }
}
