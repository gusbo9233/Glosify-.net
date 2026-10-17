using System.ComponentModel.DataAnnotations;
using Glosify.Services.Anki;

namespace Glosify.Models.ViewModels;

public sealed class AnkiIndexViewModel
{
    public required string TargetLanguage { get; init; }
    public required string Direction { get; init; }
    public required AnkiWorkspace Workspace { get; init; }
    public AnkiStudyViewModel? Study { get; init; }
}

public sealed class AnkiStudyViewModel
{
    public required AnkiStudyState State { get; init; }
    public required string TargetLanguage { get; init; }
    public AnkiStatistics? Statistics { get; init; }
    public bool AnswerRevealed { get; init; }
    public Guid ClientToken { get; init; }
}

public sealed class AddAnkiQuizForm
{
    public Guid QuizId { get; set; }
    public bool IncludeSentences { get; set; }
    [StringLength(128)] public string? TimeZoneId { get; set; }
}

public sealed class SaveAnkiItemsForm
{
    // Keys are "{itemType}:{quizId}:{itemId}". The page posts only changed rows.
    public List<string> Add { get; set; } = [];
    public List<string> Remove { get; set; } = [];
    [StringLength(128)] public string? TimeZoneId { get; set; }
}

public sealed class AddAnkiItemForm
{
    public Guid QuizId { get; set; }
    [Required] public string ItemType { get; set; } = string.Empty;
    [Required] public string ItemId { get; set; } = string.Empty;
    [StringLength(128)] public string? TimeZoneId { get; set; }
}

public sealed class RateAnkiCardForm
{
    public Guid CollectionId { get; set; }
    public Guid CardId { get; set; }
    [Required, RegularExpression("^(again|hard|good|easy)$")]
    public string Rating { get; set; } = string.Empty;
    [Required]
    public Guid? ClientToken { get; set; }
    public string RowVersion { get; set; } = string.Empty;
    public int? DurationMilliseconds { get; set; }
}
