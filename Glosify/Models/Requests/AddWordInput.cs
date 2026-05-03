using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class AddWordInput
{
    [Required]
    public Guid QuizId { get; set; }

    [Required(ErrorMessage = "Word is required.")]
    [StringLength(256, MinimumLength = 1)]
    public string Word { get; set; } = string.Empty;

    [Required(ErrorMessage = "Translation is required.")]
    [StringLength(512, MinimumLength = 1)]
    public string Translation { get; set; } = string.Empty;
}
