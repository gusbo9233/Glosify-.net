namespace Glosify.Models
{
    public class QuizWorkspaceViewModel
    {
        public Quiz SelectedQuiz { get; set; } = null!;
        public IReadOnlyList<Word> Words { get; set; } = [];
    }
}
