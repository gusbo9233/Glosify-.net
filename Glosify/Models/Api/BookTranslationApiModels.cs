using System.ComponentModel.DataAnnotations;

namespace Glosify.Models.Api;

public sealed class UpdateBookTranslationPreferenceRequest
{
    [Required]
    [StringLength(64)]
    public string? TargetLanguage { get; set; }
}

public sealed class TranslateBookPageRequest
{
    [Required]
    [StringLength(64)]
    public string? TargetLanguage { get; set; }

    [Required]
    [MinLength(1)]
    public List<BookPageSourceSegmentRequest>? Segments { get; set; }
}

public sealed class BookPageSourceSegmentRequest
{
    [Range(0, int.MaxValue)]
    public int Index { get; set; }

    [Range(0, int.MaxValue)]
    public int ParagraphIndex { get; set; }

    [Required]
    [StringLength(8000)]
    public string? SourceText { get; set; }
}
