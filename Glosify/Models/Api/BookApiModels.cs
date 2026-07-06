using Glosify.Models.Library;

namespace Glosify.Models.Api;

public sealed record BookDto(
    Guid Id,
    string Title,
    string OriginalFileName,
    int PageCount,
    string ProcessingStatus,
    string? ProcessingMessage,
    DateTimeOffset CreatedAt)
{
    public static BookDto From(BookDocument book) => new(
        book.Id, book.Title, book.OriginalFileName, book.PageCount,
        book.ProcessingStatus, book.ProcessingMessage, book.CreatedAt);
}

public sealed record BookPageDto(int PageNumber, string Text, string? ExtractionWarning);
