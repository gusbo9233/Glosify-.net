namespace Glosify.Models.Library
{
    public class BookPage
    {
        public Guid Id { get; set; }
        public Guid BookDocumentId { get; set; }
        public int PageNumber { get; set; }
        public string Text { get; set; } = string.Empty;
        public string? ExtractionWarning { get; set; }
        public BookDocument BookDocument { get; set; } = null!;
    }
}
