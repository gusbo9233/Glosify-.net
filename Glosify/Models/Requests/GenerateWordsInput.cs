using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class GenerateWordsInput
{
    [Required]
    public Guid QuizId { get; set; }

    [RegularExpression("^gemini$", ErrorMessage = "Choose a supported AI provider.")]
    public string AiProvider { get; set; } = "gemini";

    [Required(ErrorMessage = "Paste some text first so the assistant has vocabulary to extract.")]
    [StringLength(20_000, MinimumLength = 1)]
    public string Input { get; set; } = string.Empty;
}
