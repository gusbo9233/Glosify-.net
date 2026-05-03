namespace Glosify.Models.ViewModels
{
    public class QuizWorkspaceViewModel
    {
        public Quiz SelectedQuiz { get; set; } = null!;
        public IReadOnlyList<Word> Words { get; set; } = [];
        public IReadOnlyList<QuizSentenceViewModel> Sentences { get; set; } = [];
    }

    public class QuizSentenceViewModel
    {
        public string Text { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public int WordCount { get; set; }
    }

    public class QuizSettingsViewModel
    {
        public Quiz? SelectedQuiz { get; set; }
        public int AvailableWordCount { get; set; }
        public int SelectedWordCount { get; set; }
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
        public bool IsAnswerRevealed { get; set; }
        public bool IsComplete { get; set; }

        public static FlashcardQuizViewModel Empty() => new();
    }

    public class FlashcardWordViewModel
    {
        public string Id { get; set; } = string.Empty;
        public string Lemma { get; set; } = string.Empty;
        public string Translation { get; set; } = string.Empty;
        public string ExampleSentence { get; set; } = string.Empty;
        public string ExampleTranslation { get; set; } = string.Empty;
    }

    public class TypingQuizViewModel
    {
        public Quiz? SelectedQuiz { get; set; }
        public IReadOnlyList<TypingQuizWordViewModel> Words { get; set; } = [];
        public Guid QuizId { get; set; }
        public int WordCount { get; set; }
        public bool ShowsUkrainianKeyboard { get; set; }

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
