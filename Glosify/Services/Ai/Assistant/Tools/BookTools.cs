using System.ComponentModel;
using Glosify.Data;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record ListBooksArgs(
    [property: Description("Number of books to skip. Null for 0.")] int? Offset = null);

internal sealed class ListBooksTool(GlosifyContext db) : AssistantTool<ListBooksArgs>
{
    private const int PageSize = 50;

    public override string Name => "list_books";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) =>
        $"List the user's uploaded books with title, page count, language, date, and id. Use it when the user refers to a book without identifying it. Returns up to {PageSize} books per call.";

    protected override async Task<ToolResult> RunAsync(ListBooksArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var offset = Math.Max(0, args.Offset ?? 0);
        var query = db.BookDocuments.AsNoTracking().Where(book => book.UserId == context.UserId);
        if (!string.IsNullOrWhiteSpace(context.TargetLanguage))
        {
            query = query.Where(book => book.Language == context.TargetLanguage);
        }

        var total = await query.CountAsync(cancellationToken);
        var books = await query
            .OrderByDescending(book => book.CreatedAt)
            .Skip(offset)
            .Take(PageSize)
            .Select(book => new
            {
                id = book.Id,
                title = book.Title,
                language = book.Language,
                page_count = book.PageCount,
                created_at = book.CreatedAt,
            })
            .ToListAsync(cancellationToken);
        return ToolResult.Ok($"Listed {QuizContent.Count(books.Count, "book")}", new
        {
            books,
            total_count = total,
            offset,
            has_more = offset + books.Count < total,
            current_book_id = context.BookDocumentId,
        });
    }
}

internal sealed record GetBookPagesArgs(
    [property: Description("Book id. Null for the book selected for this chat.")] string? BookId = null,
    [property: Description("First page to read, counting from 1. Null for 1.")] int? FromPage = null,
    [property: Description("Maximum pages, 1 to 10. Null for 3. A long page may cut the run short; check next_page.")] int? Limit = null);

internal sealed class GetBookPagesTool(GlosifyContext db) : AssistantTool<GetBookPagesArgs>
{
    /// <summary>One call must not be able to fill the context window, however long the pages are.</summary>
    private const int MaximumCharacters = 12_000;

    public override string Name => "get_book_pages";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) =>
        "Read a run of pages from one of the user's books, in order. Defaults to the book selected for this chat. Page forward with next_page while has_more is true.";

    protected override async Task<ToolResult> RunAsync(GetBookPagesArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        var book = await BookTools.FindAsync(db, context, args.BookId, cancellationToken);
        if (book is null)
        {
            return ToolResult.Fail("Could not read the book", "Book not found. Choose a book first or pass a book_id from list_books.");
        }

        var fromPage = Math.Max(1, args.FromPage ?? 1);
        var limit = Math.Clamp(args.Limit ?? 3, 1, 10);
        var rows = await db.BookPages.AsNoTracking()
            .Where(page => page.BookDocumentId == book.Id && page.PageNumber >= fromPage)
            .OrderBy(page => page.PageNumber)
            .Take(limit)
            .Select(page => new { page.PageNumber, page.Text, page.ExtractionWarning })
            .ToListAsync(cancellationToken);
        var pages = new List<object>(rows.Count);
        var characters = 0;
        foreach (var row in rows)
        {
            if (pages.Count > 0 && characters + row.Text.Length > MaximumCharacters)
            {
                break;
            }

            var text = row.Text.Length <= MaximumCharacters ? row.Text : row.Text[..MaximumCharacters];
            pages.Add(new { page_number = row.PageNumber, text, warning = row.ExtractionWarning });
            characters += text.Length;
        }

        var nextPage = pages.Count == 0 ? fromPage : rows[pages.Count - 1].PageNumber + 1;
        var last = nextPage - 1;
        return ToolResult.Ok(
            pages.Count == 0
                ? $"Read no pages of “{book.Title}”"
                : last == fromPage ? $"Read page {fromPage} of “{book.Title}”" : $"Read pages {fromPage}–{last} of “{book.Title}”",
            new
            {
                id = book.Id,
                title = book.Title,
                pages,
                from_page = fromPage,
                page_count = book.PageCount,
                has_more = nextPage <= book.PageCount,
                next_page = nextPage,
            });
    }
}

internal sealed record SearchBookPagesArgs(
    [property: Description("Text to look for. Several words are an AND: only pages containing all of them match, so keep it short and distinctive. At most four words are used.")] string Query,
    [property: Description("Book id. Null for the book selected for this chat.")] string? BookId = null,
    [property: Description("First page to search from. Null for 1. This narrows the search; it is not paging, because matches are ranked.")] int? FromPage = null,
    [property: Description("Maximum matching pages, 1 to 20. Null for 8. When more pages match, narrow the query instead.")] int? Limit = null);

/// <summary>
/// Finds where in a book something is discussed, returning page numbers with snippets so
/// the model reads only the pages that matter.
/// </summary>
internal sealed class SearchBookPagesTool(GlosifyContext db) : AssistantTool<SearchBookPagesArgs>
{
    private const int MaximumTerms = 4;
    private const int SnippetLength = 320;
    private const int SnippetLead = 100;
    private const int CandidateWindow = 40;

    public override string Name => "search_book_pages";

    public override AssistantToolKind Kind => AssistantToolKind.Read;

    protected override string Describe(AssistantMode mode) =>
        "Search the text of one of the user's books and get back matching page numbers, each with a short snippet. Defaults to the book selected for this chat. Use it instead of paging through the book when you do not know where something is, then read the pages worth reading with get_book_pages.";

    protected override async Task<ToolResult> RunAsync(SearchBookPagesArgs args, ToolContext context, CancellationToken cancellationToken)
    {
        const string failure = "Could not search the book";
        var book = await BookTools.FindAsync(db, context, args.BookId, cancellationToken);
        if (book is null)
        {
            return ToolResult.Fail(failure, "Book not found. Choose a book first or pass a book_id from list_books.");
        }

        var search = args.Query.Trim();
        var requested = search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
        {
            return ToolResult.Fail(failure, "query is required.");
        }

        // Extra terms are dropped rather than refused, but listed back: otherwise the AND the
        // model was promised is not the AND it got.
        var terms = requested.Take(MaximumTerms).ToList();
        var ignoredTerms = requested.Skip(MaximumTerms).ToList();
        var fromPage = Math.Max(1, args.FromPage ?? 1);
        var limit = Math.Clamp(args.Limit ?? 8, 1, 20);
        var bookPages = db.BookPages.AsNoTracking().Where(page => page.BookDocumentId == book.Id);
        var query = bookPages.Where(page => page.PageNumber >= fromPage);
        foreach (var term in terms)
        {
            query = AssistantSearchQuery.WherePageContains(query, term, db.Database);
        }

        var title = $"Searched “{book.Title}” for “{search}”";
        var totalMatches = await query.CountAsync(cancellationToken);
        if (totalMatches == 0)
        {
            return ToolResult.Ok(title + " (no pages)", await DescribeMissAsync(bookPages, book, search, terms, ignoredTerms, fromPage, cancellationToken));
        }

        // Ranked in memory: counting occurrences in SQL is provider-specific. The window bounds
        // the cost, and match_count tells the model when a better page could be outside it.
        var candidates = await query
            .OrderBy(page => page.PageNumber)
            .Take(CandidateWindow)
            .Select(page => new { page.PageNumber, page.Text, page.ExtractionWarning })
            .ToListAsync(cancellationToken);
        var matches = candidates
            .Select(row => new { row.PageNumber, row.Text, row.ExtractionWarning, Hits = terms.Sum(term => CountOccurrences(row.Text, term)) })
            .OrderByDescending(row => row.Hits)
            .ThenBy(row => row.PageNumber)
            .Take(limit)
            .Select(row => new
            {
                page_number = row.PageNumber,
                snippet = BuildSnippet(row.Text, terms),
                hits = row.Hits,
                warning = row.ExtractionWarning,
            })
            .ToList();
        AssistantAnalyticsTelemetry.RecordBookSearch(terms.Count, totalMatches, matches.Count, zeroPageTerms: 0, topPageHits: matches[0].hits);
        return ToolResult.Ok($"{title} ({QuizContent.Count(totalMatches, "page")})", new
        {
            id = book.Id,
            title = book.Title,
            query = search,
            terms,
            matches,
            ignored_terms = ignoredTerms,
            from_page = fromPage,
            page_count = book.PageCount,
            match_count = totalMatches,
            returned_count = matches.Count,
            has_more = matches.Count < totalMatches,
            ranked_by = totalMatches > CandidateWindow
                ? $"Most hits first, chosen from the first {CandidateWindow} matching pages only. {totalMatches} pages match, so narrow the query rather than paging."
                : "Most hits first, across every matching page.",
        });
    }

    /// <summary>
    /// Per-term page counts over the whole book, which tell the model which word was wrong —
    /// the difference between "not in this book" and "try the stem or the book's own term".
    /// </summary>
    private async Task<object> DescribeMissAsync(
        IQueryable<BookPage> bookPages,
        BookTools.BookInfo book,
        string search,
        IReadOnlyList<string> terms,
        IReadOnlyList<string> ignoredTerms,
        int fromPage,
        CancellationToken cancellationToken)
    {
        var termPages = new List<object>(terms.Count);
        var missing = new List<string>();
        foreach (var term in terms)
        {
            var pages = await AssistantSearchQuery.WherePageContains(bookPages, term, db.Database).CountAsync(cancellationToken);
            termPages.Add(new { term, page_count = pages });
            if (pages == 0)
            {
                missing.Add(term);
            }
        }

        var hint = missing.Count == terms.Count
            ? "None of these terms appear anywhere in the book. The book may use different wording or another language — try the terms the book itself would print, or a shorter stem."
            : missing.Count > 0
                ? $"No page has all of them: {string.Join(", ", missing)} appear nowhere in the book. Search again without those terms, or with a shorter stem or the book's wording."
                : fromPage > 1
                    ? $"Every term appears in the book, but no page from page {fromPage} onward has all of them. Search again from page 1 before concluding anything is missing."
                    : "Every term appears in the book, but never together on one page. Search for the most distinctive term on its own.";
        AssistantAnalyticsTelemetry.RecordBookSearch(terms.Count, matchCount: 0, returnedCount: 0, zeroPageTerms: missing.Count, topPageHits: 0);
        return new
        {
            id = book.Id,
            title = book.Title,
            query = search,
            terms,
            matches = Array.Empty<object>(),
            ignored_terms = ignoredTerms,
            from_page = fromPage,
            page_count = book.PageCount,
            match_count = 0,
            term_pages = termPages,
            term_pages_cover = "the whole book, not only the pages from from_page onward",
            hint,
        };
    }

    private static int CountOccurrences(string text, string term)
    {
        var count = 0;
        var index = text.IndexOf(term, StringComparison.OrdinalIgnoreCase);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(term, index + term.Length, StringComparison.OrdinalIgnoreCase);
        }

        return count;
    }

    private static string BuildSnippet(string text, IReadOnlyList<string> terms)
    {
        var hit = terms
            .Select(term => text.IndexOf(term, StringComparison.OrdinalIgnoreCase))
            .Where(index => index >= 0)
            .DefaultIfEmpty(0)
            .Min();
        var start = Math.Max(0, hit - SnippetLead);
        var length = Math.Min(SnippetLength, text.Length - start);
        var snippet = text.Substring(start, length);
        return (start > 0 ? "…" : string.Empty) + snippet + (start + length < text.Length ? "…" : string.Empty);
    }
}

internal static class BookTools
{
    internal sealed record BookInfo(Guid Id, string Title, int PageCount);

    /// <summary>
    /// Ownership only: the language column is a display name and is null on older uploads, so
    /// filtering on it would hide books the user legitimately owns and selected.
    /// </summary>
    public static async Task<BookInfo?> FindAsync(
        GlosifyContext db,
        ToolContext context,
        string? bookId,
        CancellationToken cancellationToken)
    {
        var id = string.IsNullOrWhiteSpace(bookId) ? context.BookDocumentId : Guid.TryParse(bookId, out var parsed) ? parsed : null;
        if (id is null)
        {
            return null;
        }

        return await db.BookDocuments.AsNoTracking()
            .Where(book => book.Id == id && book.UserId == context.UserId)
            .Select(book => new BookInfo(book.Id, book.Title, book.PageCount))
            .SingleOrDefaultAsync(cancellationToken);
    }
}
