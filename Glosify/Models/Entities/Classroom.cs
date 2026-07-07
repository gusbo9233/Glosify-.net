namespace Glosify.Models.Entities
{
    public class Classroom
    {
        public Guid Id { get; set; }
        public string OwnerUserId { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Description { get; set; }
        public string JoinCode { get; set; } = string.Empty;
        public bool JoinCodeEnabled { get; set; } = true;
        public Guid GroupCallId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsArchived { get; set; }
    }
}
