namespace Glosify.Models.ViewModels
{
    public class QuizIndexViewModel
    {
        public IReadOnlyList<Quiz> Quizzes { get; set; } = [];
        public IReadOnlyList<Collection> Collections { get; set; } = [];
        public Collection? CurrentCollection { get; set; }
        public Collection? ParentCollection { get; set; }
        public string CurrentLanguage { get; set; } = string.Empty;
    }

    public class QuizWorkspaceViewModel
    {
        public Quiz SelectedQuiz { get; set; } = null!;
        public IReadOnlyList<Word> Words { get; set; } = [];
        public IReadOnlyList<QuizSentenceViewModel> Sentences { get; set; } = [];
        public IReadOnlyList<CustomQuizSummaryDto> CustomQuizzes { get; set; } = [];
    }

    public class QuizSentenceViewModel
    {
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
        public Collection Collection { get; set; } = null!;
        public int CollectionCount { get; set; }
        public int QuizCount { get; set; }
    }

    public class ExploreQuizCardViewModel
    {
        public Quiz Quiz { get; set; } = null!;
        public int WordCount { get; set; }
    }

    public class ExploreCollectionViewModel
    {
        public Collection Collection { get; set; } = null!;
        public int CollectionCount { get; set; }
        public int QuizCount { get; set; }
    }

    public class AssistantPanelViewModel
    {
        public Guid? QuizId { get; set; }
        public string? FocusedWordId { get; set; }
        public Guid? DocumentId { get; set; }
        public int? CurrentPage { get; set; }
        public Guid? CustomQuizId { get; set; }
        public string Title { get; set; } = "Assistant";
        public string ContextLabel { get; set; } = string.Empty;
        public string EmptyText { get; set; } = "Start a conversation.";
        public string Placeholder { get; set; } = "Ask the assistant...";
    }

    public class QuizSettingsViewModel
    {
        public Quiz? SelectedQuiz { get; set; }
        public int AvailableWordCount { get; set; }
        public int AvailableSentenceCount { get; set; }
        public int SelectedWordCount { get; set; }
        public IReadOnlyList<Word> Words { get; set; } = [];
    }

    public class FlashcardQuizViewModel
    {
        public Quiz? SelectedQuiz { get; set; }
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
        public Guid? ClassroomId { get; set; }

        public static FlashcardQuizViewModel Empty() => new();
    }

    public class FlashcardWordViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Lemma { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public string ExampleSentence { get; set; } = string.Empty;
        public string ExampleTranslation { get; set; } = string.Empty;
    }

    public class TypingQuizViewModel
    {
        public Quiz? SelectedQuiz { get; set; }
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
        public Guid? ClassroomId { get; set; }

        public static TypingQuizViewModel Empty() => new();
    }

    public class TypingQuizWordViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Prompt { get; set; } = string.Empty;
        public string Answer { get; set; } = string.Empty;
        public string ExampleSentence { get; set; } = string.Empty;
        public string ExampleTranslation { get; set; } = string.Empty;
    }
}
