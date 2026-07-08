using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public class QuizSessionSettings
{
    public Guid? QuizId { get; set; }

    [Range(1, 200, ErrorMessage = "Pick a word count between 1 and 200.")]
    public int WordCount { get; set; }

    [Range(0, 100, ErrorMessage = "Word range start must be between 0 and 100.")]
    public int WordRangeStart { get; set; }

    [Range(0, 100, ErrorMessage = "Word range end must be between 0 and 100.")]
    public int WordRangeEnd { get; set; } = 100;

    /// <summary>Comma-separated word IDs; when set, overrides the word-range slider with an exact hand-picked set.</summary>
    public string? SelectedWordIds { get; set; }

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

    [Required]
    [RegularExpression(
        "^(source-to-target|target-to-source)$",
        ErrorMessage = "Choose a valid practice direction.")]
    public string PracticeDirection { get; set; } = Glosify.Models.PracticeDirection.SourceToTarget;

    [Required]
    [RegularExpression(
        "^(words|sentences)$",
        ErrorMessage = "Choose valid practice content.")]
    public string PracticeItemType { get; set; } = Glosify.Models.PracticeItemType.Words;
}
