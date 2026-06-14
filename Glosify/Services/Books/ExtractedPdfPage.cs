namespace Glosify.Services.Books;

public sealed record ExtractedPdfPage(int PageNumber, string Text, string? Warning);
