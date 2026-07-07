namespace Glosify.Models.Entities
{
    public class ClassroomAssignment
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public Guid? LessonId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Instructions { get; set; }
        public Guid? QuizId { get; set; }
        public DateTimeOffset? DueAt { get; set; }
        public string CreatedByUserId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
