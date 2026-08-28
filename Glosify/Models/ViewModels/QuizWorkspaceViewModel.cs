using Glosify.Localization;
using Glosify.Services.Anki;

namespace Glosify.Models.ViewModels;

public class QuizIndexViewModel
{
    public IReadOnlyList<QuizCard> VisibleQuizzes { get; set; } = [];
    public IReadOnlyList<QuizLibraryCollectionCard> ChildCollections { get; set; } = [];
    public CollectionCard? CurrentCollection { get; set; }
    public CollectionCard? ParentCollection { get; set; }
    public QuizIndexPresentation Presentation { get; set; } = QuizIndexPresentation.Empty;
}

public sealed record QuizLibraryCollectionCard(
    CollectionCard Collection,
    int ChildCollectionCount,
    int QuizCount);

/// <summary>
/// Display-ready library metadata. Filtering and prompt construction happen before Razor
/// receives the model, leaving the view responsible only for markup.
/// </summary>
public sealed record QuizIndexPresentation(
    string CurrentLanguage,
    string DisplayLanguage,
    bool IsFreestyle,
    string PageTitle,
    string PageSubtitle,
    string ImportDestination,
    string JsonGenerationPrompt)
{
    public static QuizIndexPresentation Empty { get; } = new(
        string.Empty,
        string.Empty,
        false,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty);
}

public class QuizWorkspaceViewModel
{
    public QuizCard SelectedQuiz { get; set; } = null!;
    public IReadOnlyList<WordRow> Words { get; set; } = [];
    public IReadOnlyList<QuizSentenceViewModel> Sentences { get; set; } = [];
    public IReadOnlyList<AnkiCollectionSummary> AnkiCollections { get; set; } = [];
}

public class QuizSentenceViewModel
{
    public Guid Id { get; set; }
    public string Text { get; set; } = string.Empty;
    public string Translation { get; set; } = string.Empty;
    public int WordCount { get; set; }
}

public class ExploreIndexViewModel
{
    public string CurrentLanguage { get; set; } = string.Empty;
    public IReadOnlyList<ExploreCollectionCardViewModel> Collections { get; set; } = [];
    public IReadOnlyList<ExploreQuizCardViewModel> Quizzes { get; set; } = [];
}

public class ExploreCollectionCardViewModel
{
    public CollectionCard Collection { get; set; } = null!;
    public int CollectionCount { get; set; }
    public int QuizCount { get; set; }
}

public class ExploreQuizCardViewModel
{
    public QuizCard Quiz { get; set; } = null!;
    public int WordCount { get; set; }
}

public class ExploreCollectionViewModel
{
    public ExploreCollectionNode Collection { get; set; } = null!;
    public int CollectionCount { get; set; }
    public int QuizCount { get; set; }
}

public class QuizSettingsViewModel
{
    public QuizCard? SelectedQuiz { get; set; }
    public int AvailableWordCount { get; set; }
    public int AvailableSentenceCount { get; set; }
    public IReadOnlyList<WordRow> Words { get; set; } = [];
    public QuizSettingsPresentation Presentation { get; set; } = QuizSettingsPresentation.Empty;
}

public sealed record QuizSettingsLengthOption(
    int WordCount,
    int SentenceCount,
    bool IsSelected);

/// <summary>
/// Display-ready values for the quiz settings page. Keeping the count normalization,
/// option selection, and language-label choices here makes the Razor view declarative.
/// </summary>
public sealed record QuizSettingsPresentation(
    string QuizName,
    string SourceLanguage,
    string TargetLanguage,
    string ItemLabel,
    string PromptLabel,
    string AnswerLabel,
    bool IsFreestyle,
    QuizSettingsLengthOption QuickLength,
    QuizSettingsLengthOption StandardLength,
    QuizSettingsLengthOption AllLength)
{
    public static QuizSettingsPresentation Empty { get; } = new(
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        string.Empty,
        false,
        new(1, 1, true),
        new(1, 1, false),
        new(1, 1, false));

    public static QuizSettingsPresentation Create(
        QuizCard? selectedQuiz,
        int availableWordCount,
        int availableSentenceCount,
        int selectedWordCount,
        string defaultQuizName,
        string defaultSourceLanguage,
        string defaultTargetLanguage,
        string wordsLabel)
    {
        var normalizedWordCount = Math.Max(availableWordCount, 1);
        var normalizedSentenceCount = Math.Max(availableSentenceCount, 1);
        var isFreestyle = selectedQuiz?.IsFreestyle == true;
        var sourceLanguage = QuizLanguageDisplay.Name(selectedQuiz?.SourceLanguage);
        var targetLanguage = QuizLanguageDisplay.Name(selectedQuiz?.TargetLanguage);

        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            sourceLanguage = defaultSourceLanguage;
        }

        if (string.IsNullOrWhiteSpace(targetLanguage))
        {
            targetLanguage = defaultTargetLanguage;
        }

        var itemLabel = isFreestyle ? "Items" : wordsLabel;
        var promptLabel = isFreestyle ? "Prompt" : sourceLanguage;
        var answerLabel = isFreestyle ? "Answer" : targetLanguage;

        return new QuizSettingsPresentation(
            selectedQuiz?.Name ?? defaultQuizName,
            sourceLanguage,
            targetLanguage,
            itemLabel,
            promptLabel,
            answerLabel,
            isFreestyle,
            new(
                Math.Min(10, normalizedWordCount),
                Math.Min(10, normalizedSentenceCount),
                selectedWordCount <= 10),
            new(
                Math.Min(20, normalizedWordCount),
                Math.Min(20, normalizedSentenceCount),
                selectedWordCount is > 10 and <= 20),
            new(
                normalizedWordCount,
                normalizedSentenceCount,
                selectedWordCount > 20));
    }
}

public class FlashcardQuizViewModel
{
    public QuizCard? SelectedQuiz { get; set; }
    public IReadOnlyList<FlashcardWordViewModel> Cards { get; set; } = [];
    public FlashcardWordViewModel? CurrentCard { get; set; }
    public string SessionState { get; set; } = string.Empty;
    public string SessionId { get; set; } = string.Empty;
    public Guid QuizId { get; set; }
    public int CurrentIndex { get; set; }
    public int CurrentCardNumber { get; set; }
    public int TotalCards { get; set; }
    public int CompletedCards { get; set; }
    public int RememberedCount { get; set; }
    public int AgainCount { get; set; }
    public int SkippedCount { get; set; }
    public int ScorePercent { get; set; }
    public int ProgressPercent { get; set; }
    public int WordCount { get; set; }
    public int WordRangeStart { get; set; }
    public int WordRangeEnd { get; set; } = 100;
    public string? SelectedWordIds { get; set; }
    public string PracticeDirection { get; set; } = Glosify.Models.PracticeDirection.SourceToTarget;
    public string PromptLanguage { get; set; } = string.Empty;
    public string AnswerLanguage { get; set; } = string.Empty;
    public string DirectionLabel { get; set; } = string.Empty;
    public string PracticeItemType { get; set; } = Glosify.Models.PracticeItemType.Words;
    public string ItemSingularLabel { get; set; } = "word";
    public string ItemPluralLabel { get; set; } = "words";
    public string CardLabel { get; set; } = "Word";
    public bool IsAnswerRevealed { get; set; }
    public bool IsComplete { get; set; }

    public static FlashcardQuizViewModel Empty() => new();
}

public class FlashcardWordViewModel
{
    public string Prompt { get; set; } = string.Empty;
    public string Answer { get; set; } = string.Empty;
    public string ExampleSentence { get; set; } = string.Empty;
    public string ExampleTranslation { get; set; } = string.Empty;
}

public class TypingQuizViewModel
{
    public QuizCard? SelectedQuiz { get; set; }
    public TypingQuizWordViewModel? CurrentWord { get; set; }
    public string SessionId { get; set; } = string.Empty;
    public Guid QuizId { get; set; }
    public int CurrentWordNumber { get; set; }
    public int TotalWords { get; set; }
    public int CompletedWords { get; set; }
    public int CorrectCount { get; set; }
    public int IncorrectCount { get; set; }
    public int ScorePercent { get; set; }
    public int ProgressPercent { get; set; }
    public int WordCount { get; set; }
    public int WordRangeStart { get; set; }
    public int WordRangeEnd { get; set; } = 100;
    public string? SelectedWordIds { get; set; }
    public string PracticeDirection { get; set; } = Glosify.Models.PracticeDirection.SourceToTarget;
    public string PromptLanguage { get; set; } = string.Empty;
    public string AnswerLanguage { get; set; } = string.Empty;
    public string DirectionLabel { get; set; } = string.Empty;
    public string PracticeItemType { get; set; } = Glosify.Models.PracticeItemType.Words;
    public string ItemSingularLabel { get; set; } = "word";
    public string ItemPluralLabel { get; set; } = "words";
    public string CardLabel { get; set; } = "Word";
    public bool ShowsUkrainianKeyboard { get; set; }
    public bool IsComplete { get; set; }

    public static TypingQuizViewModel Empty() => new();
}

public class TypingQuizWordViewModel
{
    public string Prompt { get; set; } = string.Empty;
}
