using Glosify.Models.Entities;
using Glosify.Models.Library;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;

namespace Glosify.Services.Ai.Assistant.Tools;

/// <summary>
/// Keeps assistant text search case-insensitive under both SQL Server and the in-memory
/// provider used by focused tool tests.
/// </summary>
internal static class AssistantSearchQuery
{
    // SQL Server cannot translate the StringComparison overloads. Pin the comparison here
    // instead of inheriting the deployment database's collation. Accent sensitivity matches
    // OrdinalIgnoreCase's treatment of accents, while _SC retains supplementary characters.
    internal const string SqlServerCaseInsensitiveCollation = "Latin1_General_100_CI_AS_SC";

    internal static IQueryable<BookPage> WherePageContains(
        IQueryable<BookPage> query,
        string term,
        DatabaseFacade database) =>
        database.IsSqlServer()
            ? query.Where(page => EF.Functions
                .Collate(page.Text, SqlServerCaseInsensitiveCollation)
                .Contains(term))
            : query.Where(page => page.Text.Contains(term, StringComparison.OrdinalIgnoreCase));

    internal static IQueryable<Word> WhereWordContains(
        IQueryable<Word> query,
        string term,
        DatabaseFacade database) =>
        database.IsSqlServer()
            ? query.Where(word =>
                EF.Functions.Collate(word.Lemma, SqlServerCaseInsensitiveCollation).Contains(term)
                || EF.Functions.Collate(word.Translation, SqlServerCaseInsensitiveCollation).Contains(term))
            : query.Where(word =>
                word.Lemma.Contains(term, StringComparison.OrdinalIgnoreCase)
                || word.Translation.Contains(term, StringComparison.OrdinalIgnoreCase));
}
