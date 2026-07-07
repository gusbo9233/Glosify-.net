namespace Glosify.Models.Entities
{
    public class QuizAttemptItem
    {
        public Guid Id { get; set; }
        public Guid QuizAttemptId { get; set; }
        public string Prompt { get; set; } = string.Empty;
        public string ExpectedAnswer { get; set; } = string.Empty;
        public string? GivenAnswer { get; set; }
        public bool IsCorrect { get; set; }
        public int Sequence { get; set; }
    }
}
