namespace Glosify.Models
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
}
