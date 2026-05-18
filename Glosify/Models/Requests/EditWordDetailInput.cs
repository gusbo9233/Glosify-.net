using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Requests;

public sealed class EditWordDetailInput
{
    [Required]
    public string Id { get; set; } = string.Empty;

    public string ExampleSentence { get; set; } = string.Empty;
    public string ExampleSentenceTranslation { get; set; } = string.Empty;
    public string Explanation { get; set; } = string.Empty;
    public string Variants { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
}
