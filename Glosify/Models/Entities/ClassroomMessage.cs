namespace Glosify.Models.Entities
{
    public class ClassroomMessage
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ClassroomMessageKind Kind { get; set; }
        public string Body { get; set; } = string.Empty;
        public bool IsPinned { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? EditedAt { get; set; }
        public bool IsDeleted { get; set; }
    }
}
