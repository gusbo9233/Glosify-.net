namespace Glosify.Models.Entities
{
    public class ClassroomLesson
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string? Description { get; set; }
        public DateTimeOffset? ScheduledAt { get; set; }
        public string CreatedByUserId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
