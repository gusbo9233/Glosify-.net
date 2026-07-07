namespace Glosify.Models.Entities
{
    public class ClassroomContent
    {
        public Guid Id { get; set; }
        public Guid ClassroomId { get; set; }
        public ClassroomContentType ContentType { get; set; }
        public Guid? QuizId { get; set; }
        public Guid? BookDocumentId { get; set; }
        public string SharedByUserId { get; set; } = string.Empty;
        public DateTimeOffset SharedAt { get; set; }
        public string? Note { get; set; }
    }
}
