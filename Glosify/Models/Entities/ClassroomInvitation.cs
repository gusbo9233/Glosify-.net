namespace Glosify.Models.Entities
{
    public class ClassroomInvitation
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public string Email { get; set; } = string.Empty;
        public string InvitedByUserId { get; set; } = string.Empty;
        public ClassroomRole Role { get; set; } = ClassroomRole.Student;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset? AcceptedAt { get; set; }
        public string? AcceptedByUserId { get; set; }
    }
}
