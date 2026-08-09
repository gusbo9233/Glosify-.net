using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListBooksTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_books",
        "List the user's uploaded books with title, page count, language, date, and id. Use this when the user refers to a book without identifying it. Returns up to 50 books per call.",
        BuildSchema(new Dictionary<string, object>
        {
            ["offset"] = IntegerProp("Optional number of books to skip. Defaults to 0."),
        }));

    public AgentToolDeclaration Declaration => DeclarationValue;

    private readonly GlosifyContext _context;

    public ListBooksTool(GlosifyContext context) => _context = context;

    public async Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken)
    {
        const int pageSize = 50;
        var offset = GetOffset(args);
        var language = context.CurrentLanguage;
        var query = _context.BookDocuments
            .AsNoTracking()
            .Where(book => book.UserId == context.UserId);
        if (!string.IsNullOrWhiteSpace(language))
        {
            query = query.Where(book => book.Language == language);
        }

        var total = await query.CountAsync(cancellationToken);
        var books = await query
            .OrderByDescending(book => book.CreatedAt)
            .Skip(offset)
            .Take(pageSize)
            .Select(book => new
            {
                id = book.Id,
                title = book.Title,
                language = book.Language,
                page_count = book.PageCount,
                created_at = book.CreatedAt,
                updated_at = book.UpdatedAt,
            })
            .ToListAsync(cancellationToken);
        return new
        {
            books,
            total_count = total,
            offset,
            has_more = offset + books.Count < total,
            current_book_id = context.BookDocumentId,
        };
    }
}
