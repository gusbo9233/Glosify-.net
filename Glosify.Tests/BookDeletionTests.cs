using Azure.Storage.Blobs.Models;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Deleting a book has to clear the assistant chats bound to it. The FK on
/// assistant_threads.context_book_document_id is NO ACTION, so a chat still pointing at
/// the book would otherwise make the delete fail outright.
/// </summary>
public sealed class BookDeletionTests
{
    [Fact]
    public async Task Deleting_a_book_removes_it_with_its_pages_and_translations()
    {
        await using var context = CreateContext();
        var bookId = await SeedBookAsync(context, "user-1");
        var storage = new RecordingBookFileStorage();

        var deleted = await CreateService(context, storage).DeleteAsync(bookId, "user-1");

        Assert.True(deleted);
        Assert.Empty(await context.BookDocuments.ToListAsync());
        Assert.Empty(await context.BookPages.ToListAsync());
        Assert.Empty(await context.BookPageTranslations.ToListAsync());
    }

    [Fact]
    public async Task Deleting_a_book_detaches_the_chats_that_used_it()
    {
        await using var context = CreateContext();
        var bookId = await SeedBookAsync(context, "user-1");
        var otherBookId = await SeedBookAsync(context, "user-1", title: "Another book");
        var bound = CreateThread("user-1", bookId);
        var untouched = CreateThread("user-1", otherBookId);
        context.AssistantThreads.AddRange(bound, untouched);
        await context.SaveChangesAsync();

        await CreateService(context, new RecordingBookFileStorage()).DeleteAsync(bookId, "user-1");

        // The conversation survives; only its link to the deleted book goes.
        Assert.Equal(2, await context.AssistantThreads.CountAsync());
        Assert.Null((await context.AssistantThreads.SingleAsync(t => t.Id == bound.Id)).ContextBookDocumentId);
        Assert.Equal(
            otherBookId,
            (await context.AssistantThreads.SingleAsync(t => t.Id == untouched.Id)).ContextBookDocumentId);
    }

    [Fact]
    public async Task Deleting_a_book_removes_its_blob()
    {
        await using var context = CreateContext();
        var bookId = await SeedBookAsync(context, "user-1");
        var storage = new RecordingBookFileStorage();

        await CreateService(context, storage).DeleteAsync(bookId, "user-1");

        Assert.Equal([$"books/{bookId}.pdf"], storage.Deleted);
    }

    [Fact]
    public async Task Another_users_book_is_left_alone()
    {
        await using var context = CreateContext();
        var bookId = await SeedBookAsync(context, "owner");
        var storage = new RecordingBookFileStorage();

        var deleted = await CreateService(context, storage).DeleteAsync(bookId, "intruder");

        Assert.False(deleted);
        Assert.Single(await context.BookDocuments.ToListAsync());
        Assert.Empty(storage.Deleted);
    }

    [Fact]
    public async Task Deleting_a_missing_book_reports_failure_rather_than_throwing()
    {
        await using var context = CreateContext();

        var deleted = await CreateService(context, new RecordingBookFileStorage())
            .DeleteAsync(Guid.NewGuid(), "user-1");

        Assert.False(deleted);
    }

    // The row is already gone by the time the blob is touched. Losing the blob is a
    // storage leak; failing the whole delete would leave a book the user cannot remove.
    [Fact]
    public async Task A_failing_blob_delete_does_not_fail_the_whole_delete()
    {
        await using var context = CreateContext();
        var bookId = await SeedBookAsync(context, "user-1");

        var deleted = await CreateService(context, new ThrowingBookFileStorage()).DeleteAsync(bookId, "user-1");

        Assert.True(deleted);
        Assert.Empty(await context.BookDocuments.ToListAsync());
    }

    private static BookDocumentService CreateService(GlosifyContext context, IBookFileStorage storage) =>
        new(context,
            storage,
            new UnusedPdfTextExtractionService(),
            new StaticLanguageContext(),
            NullLogger<BookDocumentService>.Instance);

    private static async Task<Guid> SeedBookAsync(
        GlosifyContext context,
        string userId,
        string title = "Polish Reader")
    {
        var bookId = Guid.NewGuid();
        var page = new BookPage
        {
            Id = Guid.NewGuid(),
            BookDocumentId = bookId,
            PageNumber = 1,
            Text = "Rozdział pierwszy.",
        };
        page.Translations.Add(new BookPageTranslation
        {
            Id = Guid.NewGuid(),
            BookPageId = page.Id,
            TargetLanguage = "en",
            SourceTextHash = "hash",
            Model = "test-model",
            SegmentsJson = "[]",
            SchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow,
        });

        context.BookDocuments.Add(new BookDocument
        {
            Id = bookId,
            UserId = userId,
            Title = title,
            OriginalFileName = "reader.pdf",
            BlobName = $"books/{bookId}.pdf",
            Language = "Polish",
            PageCount = 1,
            ProcessingStatus = "Ready",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Pages = [page],
        });
        await context.SaveChangesAsync();
        return bookId;
    }

    private static AssistantThread CreateThread(string userId, Guid bookId) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Language = "Polish",
        Title = "Reading notes",
        ContextBookDocumentId = bookId,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private sealed class RecordingBookFileStorage : IBookFileStorage
    {
        public List<string> Deleted { get; } = [];

        public Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default)
        {
            Deleted.Add(blobName);
            return Task.CompletedTask;
        }

        public Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<BlobProperties> GetPropertiesAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingBookFileStorage : IBookFileStorage
    {
        public Task DeleteIfExistsAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromException(new InvalidOperationException("Storage is unreachable."));

        public Task<string> UploadAsync(Stream content, string blobName, string contentType, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenReadAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> ExistsAsync(string blobName, CancellationToken cancellationToken = default) =>
            Task.FromResult(true);

        public Task<BlobProperties> GetPropertiesAsync(string blobName, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class UnusedPdfTextExtractionService : IPdfTextExtractionService
    {
        public Task<IReadOnlyList<ExtractedPdfPage>> ExtractPagesAsync(Stream pdf, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticLanguageContext : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
