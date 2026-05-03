using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class CreateQuizInput
{
    [Required(ErrorMessage = "Name is required.")]
    [StringLength(120, MinimumLength = 1)]
    public string Name { get; set; } = string.Empty;

    [Required(ErrorMessage = "Source language is required.")]
    [StringLength(64, MinimumLength = 1)]
    public string SourceLanguage { get; set; } = string.Empty;

    [Required(ErrorMessage = "Target language is required.")]
    [StringLength(64, MinimumLength = 1)]
    public string TargetLanguage { get; set; } = string.Empty;
}
