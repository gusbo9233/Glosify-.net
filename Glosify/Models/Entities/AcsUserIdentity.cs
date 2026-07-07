namespace Glosify.Models.Entities
{
    public class AcsUserIdentity
    {
        public string UserId { get; set; } = string.Empty;
        public string AcsUserId { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
    }
}
