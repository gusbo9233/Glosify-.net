namespace Glosify.Models.Entities
{
    public class QuizAttempt
    {
        public Guid Id { get; set; }
        public Guid QuizId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public Guid? ClassroomId { get; set; }
        public string Mode { get; set; } = string.Empty;
        public string? PracticeDirection { get; set; }
        public string? PracticeItemType { get; set; }
        public int TotalItems { get; set; }
        public int CorrectCount { get; set; }
        public int IncorrectCount { get; set; }
        public int SkippedCount { get; set; }
        public DateTimeOffset StartedAt { get; set; }
        public DateTimeOffset CompletedAt { get; set; }

        public List<QuizAttemptItem> Items { get; set; } = [];
    }
}
