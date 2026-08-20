using System.ComponentModel.DataAnnotations;
using Glosify.Services.Anki;

namespace Glosify.Models.ViewModels;

public sealed class AnkiIndexViewModel
{
    public IReadOnlyList<AnkiCollectionSummary> Collections { get; init; } = [];
    public IReadOnlyList<string> SourceLanguages { get; init; } = [];
    public required string TargetLanguage { get; init; }
    public bool CreateDialogOpen { get; init; }
}

public sealed class AnkiCollectionViewModel
{
    public required AnkiCollectionDetails Details { get; init; }
    public required AnkiStatistics Statistics { get; init; }
}

public sealed class AnkiStudyViewModel
{
    public required AnkiStudyState State { get; init; }
    public bool AnswerRevealed { get; init; }
    public Guid ClientToken { get; init; }
}

public sealed class CreateAnkiCollectionForm
{
    [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
    [Required, StringLength(64)] public string SourceLanguage { get; set; } = string.Empty;
    [StringLength(128)] public string TimeZoneId { get; set; } = "UTC";
}

public class AddAnkiQuizForm
{
    public Guid CollectionId { get; set; }
    public Guid QuizId { get; set; }
    public bool WordsSourceToTarget { get; set; }
    public bool WordsTargetToSource { get; set; }
    public bool SentencesSourceToTarget { get; set; }
    public bool SentencesTargetToSource { get; set; }
}

public sealed class CreateAnkiFromQuizForm : AddAnkiQuizForm
{
    [Required, StringLength(160)] public string Name { get; set; } = string.Empty;
    [StringLength(128)] public string TimeZoneId { get; set; } = "UTC";
}

public sealed class AddAnkiItemForm
{
    public Guid CollectionId { get; set; }
    public Guid QuizId { get; set; }
    [Required] public string ItemType { get; set; } = string.Empty;
    [Required] public string ItemId { get; set; } = string.Empty;
    public bool SourceToTarget { get; set; }
    public bool TargetToSource { get; set; }
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
