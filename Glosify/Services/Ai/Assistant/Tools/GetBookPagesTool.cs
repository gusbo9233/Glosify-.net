using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class GetBookPagesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "get_book_pages",
        "Read a bounded run of pages from one of the user's books. Defaults to the book selected for this chat. Pages come back in order, so ask for the range you need and page forward with next_page while has_more is true.",
        BuildSchema(new Dictionary<string, object>
        {
            ["book_id"] = StringProp("Optional book id. Omit to use the book selected for this chat."),
            ["from_page"] = IntegerProp("Optional first page number to read, counting from 1. Defaults to 1."),
            ["limit"] = IntegerProp("Optional maximum pages from 1 to 10. Defaults to 3. A long page may cut the run short; check next_page."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public GetBookPagesTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        var idText = FirstNonBlank(GetString(args, "book_id"), context.BookDocumentId?.ToString());
        if (!Guid.TryParse(idText, out var bookId))
        {
            return new { error = "Choose a book first or provide a valid book_id." };
        }
        var fromPage = GetBoundedInt(args, "from_page", 1, 1, int.MaxValue);
        var limit = GetBoundedInt(args, "limit", 3, 1, 10);

        // Ownership only, matching the chat's book context: the language column is a
        // display name and is null on older uploads, so filtering here would hide books
        // the user legitimately owns and selected.
        var book = await _context.BookDocuments
            .AsNoTracking()
            .Where(item => item.Id == bookId && item.UserId == context.UserId)
            .Select(item => new { item.Id, item.Title, item.PageCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (book is null)
        {
            return new { error = "Book not found." };
        }

        var rows = await _context.BookPages
            .AsNoTracking()
            .Where(page => page.BookDocumentId == book.Id && page.PageNumber >= fromPage)
            .OrderBy(page => page.PageNumber)
            .Take(limit)
            .Select(page => new { page.PageNumber, page.Text, page.ExtractionWarning })
            .ToListAsync(cancellationToken);

        // Same budget as get_saved_transcript: one call must not be able to fill the
        // context window, however long the pages turn out to be.
        const int maximumCharacters = 12_000;
        var pages = new List<object>(rows.Count);
        var characters = 0;
        foreach (var row in rows)
        {
            if (pages.Count > 0 && characters + row.Text.Length > maximumCharacters)
            {
                break;
            }
            var text = row.Text.Length <= maximumCharacters
                ? row.Text
                : row.Text[..maximumCharacters];
            pages.Add(new
            {
                page_number = row.PageNumber,
                text,
                warning = row.ExtractionWarning,
            });
            characters += text.Length;
        }

        var nextPage = pages.Count == 0 ? fromPage : rows[pages.Count - 1].PageNumber + 1;
        return new
        {
            id = book.Id,
            title = book.Title,
            pages,
            from_page = fromPage,
            page_count = book.PageCount,
            has_more = nextPage <= book.PageCount,
            next_page = nextPage,
        };
    }
}
