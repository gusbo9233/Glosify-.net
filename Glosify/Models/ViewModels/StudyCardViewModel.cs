namespace Glosify.Models.ViewModels;

public sealed record StudyRating(string Value, string LabelKey, string? Interval = null, int? ShortcutKey = null);
public sealed record StudyCardViewModel(
    string Prompt, string Answer, string PromptLanguage, string AnswerLanguage, bool PromptIsTarget,
    bool Revealed, string RevealUrl, string RateUrl,
    IReadOnlyDictionary<string, string> Fields, IReadOnlyList<StudyRating> Ratings,
    string RightRating = "good", bool Pronunciation = true,
    string? Example = null, string? ExampleTranslation = null, string? Label = null,
    bool ShowRatingShortcuts = false);
