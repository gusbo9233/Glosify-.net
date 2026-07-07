namespace Glosify.Models.Entities
{
    public class ClassroomMembership
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public string UserId { get; set; } = string.Empty;
        public ClassroomRole Role { get; set; }
        public DateTimeOffset JoinedAt { get; set; }
        public DateTimeOffset? LastChatReadAt { get; set; }
    }
}
