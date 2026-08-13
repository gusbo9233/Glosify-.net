using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Library;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using static Glosify.Services.Ai.Assistant.Tools.ToolArguments;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Finds where in a book something is discussed, so the model does not have to page
/// through it with get_book_pages to answer a question about page 140.
/// </summary>
/// <remarks>
/// Snippets rather than whole pages: the point of this tool is to locate material cheaply
/// and then read the pages that matter with get_book_pages. Returning full text here would
/// cost as much as paging and defeat that.
/// </remarks>
internal sealed class SearchBookPagesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "search_book_pages",
        "Search the text of one of the user's books and get back the page numbers that match, each with a short snippet. Defaults to the book selected for this chat. Use this instead of paging through the book with get_book_pages when the user asks about a topic, word, or exercise whose page you do not know, then call get_book_pages for the pages worth reading in full.",
        BuildSchema(new Dictionary<string, object>
        {
            ["query"] = StringProp("Text to look for. Several words are treated as an AND: only pages containing all of them match, so keep it short and distinctive. At most four words are used; any beyond that are dropped and listed back in ignored_terms."),
            ["book_id"] = StringProp("Optional book id. Omit to use the book selected for this chat."),
            ["from_page"] = IntegerProp("Optional first page number to search from, counting from 1. Defaults to 1. This narrows the search to part of the book; it is not a way to page through results, because matches come back ranked rather than in page order."),
            ["limit"] = IntegerProp("Optional maximum matching pages from 1 to 20. Defaults to 8. When more pages match than this, narrow the query instead of raising the limit."),
        }, required: ["query"]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    /// <summary>Terms beyond this are ignored: every extra term is another LIKE over the book.</summary>
    private const int MaximumTerms = 4;

    /// <summary>Characters of page text shown around the first hit on a page.</summary>
    private const int SnippetLength = 320;

    /// <summary>How much of the snippet sits before the hit, so the match reads in context.</summary>
    private const int SnippetLead = 100;

    /// <summary>How many matching pages are pulled back to rank by hit count.</summary>
    private const int CandidateWindow = 40;

    private readonly GlosifyContext _context;

    public SearchBookPagesTool(GlosifyContext context) => _context = context;

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

        var search = GetString(args, "query")?.Trim();
        if (string.IsNullOrWhiteSpace(search))
        {
            return new { error = "query is required." };
        }
        var requested = search
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(term => term.ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (requested.Count == 0)
        {
            return new { error = "query is required." };
        }
        // Over the cap the extra terms are dropped rather than refused: a long query still
        // returns useful pages, and refusing would cost a round-trip to learn a limit. What
        // must not happen is dropping them silently, because then the AND the model was
        // promised is not the AND it got — so the surplus comes back in ignored_terms.
        var terms = requested.Take(MaximumTerms).ToList();
        var ignoredTerms = requested.Skip(MaximumTerms).ToList();

        var fromPage = GetBoundedInt(args, "from_page", 1, 1, int.MaxValue);
        var limit = GetBoundedInt(args, "limit", 8, 1, 20);

        // Ownership only, matching get_book_pages: the language column is a display name
        // and is null on older uploads, so filtering on it would hide books the user owns.
        var book = await _context.BookDocuments
            .AsNoTracking()
            .Where(item => item.Id == bookId && item.UserId == context.UserId)
            .Select(item => new { item.Id, item.Title, item.PageCount })
            .SingleOrDefaultAsync(cancellationToken);
        if (book is null)
        {
            return new { error = "Book not found." };
        }

        var bookPages = _context.BookPages
            .AsNoTracking()
            .Where(page => page.BookDocumentId == book.Id);
        var query = bookPages.Where(page => page.PageNumber >= fromPage);
        foreach (var term in terms)
        {
            var needle = term;
            query = query.Where(page => page.Text.ToLower().Contains(needle));
        }

        var totalMatches = await query.CountAsync(cancellationToken);
        if (totalMatches == 0)
        {
            return await DescribeMissAsync(
                bookPages,
                book.Id,
                book.Title,
                book.PageCount,
                search,
                terms,
                ignoredTerms,
                fromPage,
                cancellationToken);
        }

        // Ranking happens here rather than in SQL because counting occurrences in a
        // translatable expression is provider-specific, and the tests run on the in-memory
        // provider, which would evaluate any such expression client-side and hide a
        // translation failure against SQL Server. The window bounds what that costs: only
        // a query matching more pages than the window can have a better page ranked out,
        // and match_count tells the model when it is in that case.
        var candidates = await query
            .OrderBy(page => page.PageNumber)
            .Take(CandidateWindow)
            .Select(page => new { page.PageNumber, page.Text, page.ExtractionWarning })
            .ToListAsync(cancellationToken);

        var matches = candidates
            .Select(row => new
            {
                row.PageNumber,
                row.Text,
                row.ExtractionWarning,
                Hits = terms.Sum(term => CountOccurrences(row.Text, term)),
            })
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

        AssistantAnalyticsTelemetry.RecordBookSearch(
            terms.Count,
            totalMatches,
            matches.Count,
            zeroPageTerms: 0,
            topPageHits: matches.Count == 0 ? 0 : matches[0].hits);

        return new
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
                ? $"Most hits first, chosen from the first {CandidateWindow} matching pages only. {totalMatches} pages match, so a denser page later in the book cannot be seen from here — narrow the query rather than paging."
                : "Most hits first, across every matching page.",
        };
    }

    /// <summary>
    /// What to say when nothing matched. An empty list is a dead end; a per-term page count
    /// tells the model which word was wrong, which is the difference between "not in this
    /// book" and "try the stem, or the term the book actually uses".
    /// </summary>
    private async Task<object> DescribeMissAsync(
        IQueryable<BookPage> bookPages,
        Guid bookId,
        string title,
        int pageCount,
        string search,
        IReadOnlyList<string> terms,
        IReadOnlyList<string> ignoredTerms,
        int fromPage,
        CancellationToken cancellationToken)
    {
        // Counted over the whole book, never over the from_page window the search used.
        // Scoping these to the window made the tool report a term as absent when it was
        // only earlier in the book, which is the one thing a miss must never say: it is
        // what would let the model tell a user their textbook does not cover something.
        var termPages = new List<object>(terms.Count);
        var missing = new List<string>();
        foreach (var term in terms)
        {
            var needle = term;
            var hitPages = await bookPages.CountAsync(
                page => page.Text.ToLower().Contains(needle), cancellationToken);
            termPages.Add(new { term, page_count = hitPages });
            if (hitPages == 0)
            {
                missing.Add(term);
            }
        }

        var hint = missing.Count == terms.Count
            ? "None of these terms appear anywhere in the book. The book may use different wording, or be written in another language than your query — try the terms the book itself would use, or a shorter stem of the word."
            : missing.Count > 0
                ? $"No page has all of them: {string.Join(", ", missing)} appear nowhere in the book. Search again without those terms, or replace them with a shorter stem or the wording the book uses."
                : fromPage > 1
                    ? $"Every term appears in the book, but no page from page {fromPage} onward has all of them. The counts above cover the whole book, so search again from page 1 before concluding anything is missing."
                    : "Every term appears in the book, but never together on one page. Search for the most distinctive term on its own.";

        AssistantAnalyticsTelemetry.RecordBookSearch(
            terms.Count,
            matchCount: 0,
            returnedCount: 0,
            zeroPageTerms: missing.Count,
            topPageHits: 0);

        return new
        {
            id = bookId,
            title,
            query = search,
            terms,
            matches = Array.Empty<object>(),
            ignored_terms = ignoredTerms,
            from_page = fromPage,
            page_count = pageCount,
            match_count = 0,
            returned_count = 0,
            has_more = false,
            term_pages = termPages,
            term_pages_cover = "the whole book, not only the pages from from_page onward",
            hint,
        };
    }

    /// <summary>Non-overlapping occurrences of a term, the ranking signal.</summary>
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

    /// <summary>
    /// A window of page text around the earliest term hit. Only whole matches count, so a
    /// snippet always contains at least one of the words the user asked about.
    /// </summary>
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
        if (start > 0)
        {
            snippet = "…" + snippet;
        }
        if (start + length < text.Length)
        {
            snippet += "…";
        }
        return snippet;
    }
}
