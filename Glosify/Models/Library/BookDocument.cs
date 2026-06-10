namespace Glosify.Models.Library
{
    public class BookDocument
    {
        public Guid Id { get; set; }
        public string UserId { get; set; } = string.Empty;
        public string Title { get; set; } = string.Empty;
        public string OriginalFileName { get; set; } = string.Empty;
        public string BlobName { get; set; } = string.Empty;
        public int PageCount { get; set; }
        public string ProcessingStatus { get; set; } = "Ready";
        public string? ProcessingMessage { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }

        public ICollection<BookPage> Pages { get; set; } = [];
    }
}
