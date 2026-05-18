using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class CreateWordDetailInput
{
    [Required]
    public string SourceLanguage { get; set; } = string.Empty;

    [Required]
    public string TargetLanguage { get; set; } = string.Empty;

    [Required]
    public string Word { get; set; } = string.Empty;

    [Required]
    public string Translation { get; set; } = string.Empty;

    public string ExampleSentence { get; set; } = string.Empty;
    public string ExampleSentenceTranslation { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Variants { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}
