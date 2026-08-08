using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Books;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class BookPageTranslationServiceTests
{
    private static readonly BookPageSourceSegment[] SourceSegments =
    [
        new(0, 0, "Hello there."),
        new(1, 0, "How are you?"),
    ];

    [Fact]
    public async Task Cache_miss_invokes_page_translation_model_once_and_repeat_is_free()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var client = new FakeGenerativeAiClient();
        var service = CreateService(context, client);

        var generated = await service.TranslatePageAsync(
            document.Id, 1, "owner", "swedish", SourceSegments);
        var cached = await service.TranslatePageAsync(
            document.Id, 1, "owner", "Swedish", SourceSegments);

        Assert.False(generated.Cached);
        Assert.True(cached.Cached);
        Assert.Equal("Swedish", generated.TargetLanguage);
        Assert.Equal("Swedish translation 0", generated.Segments[0].Translation);
        Assert.Equal(1, client.CallCount);
        var usage = Assert.Single(client.UsageContexts);
        Assert.Equal(AiUsageFeatures.PageTranslation, usage.Feature);
        Assert.Equal("translate_book_page", usage.Operation);
        Assert.Equal("gpt-5.4-mini", Assert.Single(client.Models));
        Assert.Single(await context.BookPageTranslations.ToListAsync());
    }

    [Fact]
    public async Task Target_language_and_source_hash_create_separate_cache_entries()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var client = new FakeGenerativeAiClient();
        var service = CreateService(context, client);

        await service.TranslatePageAsync(document.Id, 1, "owner", "Swedish", SourceSegments);
        await service.TranslatePageAsync(document.Id, 1, "owner", "German", SourceSegments);
        await service.TranslatePageAsync(
            document.Id,
            1,
            "owner",
            "Swedish",
            [SourceSegments[0], new(1, 0, "How is everything?")]);

        Assert.Equal(3, client.CallCount);
        Assert.Equal(3, await context.BookPageTranslations.CountAsync());
    }

    [Fact]
    public async Task Dense_page_is_batched_but_cached_as_one_complete_page()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var segments = Enumerable.Range(0, 5)
            .Select(index => new BookPageSourceSegment(
                index,
                0,
                $"Sentence {index}: {new string((char)('a' + index), 1_750)}"))
            .ToArray();
        var client = new FakeGenerativeAiClient
        {
            PromptResponseFactory = (_, prompt) =>
            {
                var input = prompt[(prompt.LastIndexOf("Input segments:", StringComparison.Ordinal) +
                    "Input segments:".Length)..];
                return new BookPageTranslationAiResponse
                {
                    DetectedSourceLanguage = "English",
                    Segments = Regex.Matches(input, "\\\"index\\\":(?<index>\\d+)")
                        .Select(match => new BookPageTranslationAiSegment
                        {
                            Index = int.Parse(match.Groups["index"].Value),
                            Translation = $"Translation {match.Groups["index"].Value}",
                        })
                        .ToList(),
                };
            },
        };

        var result = await CreateService(context, client).TranslatePageAsync(
            document.Id, 1, "owner", "Polish", segments);

        Assert.Equal(2, client.CallCount);
        Assert.Equal(5, result.Segments.Count);
        Assert.Single(await context.BookPageTranslations.ToListAsync());
        Assert.Single(client.UsageContexts.Select(context => context.OperationId).Distinct());
    }

    [Fact]
    public async Task Concurrent_identical_requests_share_one_generation()
    {
        var root = new InMemoryDatabaseRoot();
        var databaseName = Guid.NewGuid().ToString("N");
        await using (var seedContext = CreateContext(databaseName, root))
        {
            await SeedBookAsync(seedContext, "owner", documentId: KnownDocumentId);
        }

        await using var firstContext = CreateContext(databaseName, root);
        await using var secondContext = CreateContext(databaseName, root);
        var client = new FakeGenerativeAiClient { BlockFirstCall = true };
        var coordinator = new BookPageTranslationCoordinator();
        var firstService = CreateService(firstContext, client, coordinator);
        var secondService = CreateService(secondContext, client, coordinator);

        var first = firstService.TranslatePageAsync(
            KnownDocumentId, 1, "owner", "Swedish", SourceSegments);
        await client.FirstCallEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = secondService.TranslatePageAsync(
            KnownDocumentId, 1, "owner", "Swedish", SourceSegments);
        client.ReleaseFirstCall.TrySetResult();

        var results = await Task.WhenAll(first, second);

        Assert.Equal(1, client.CallCount);
        Assert.Contains(results, result => !result.Cached);
        Assert.Contains(results, result => result.Cached);
    }

    [Fact]
    public async Task Foreign_book_and_malformed_input_never_invoke_ai_or_cache()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var client = new FakeGenerativeAiClient();
        var service = CreateService(context, client);

        await Assert.ThrowsAsync<BookPageTranslationNotFoundException>(() =>
            service.TranslatePageAsync(document.Id, 1, "other-user", "Swedish", SourceSegments));
        await Assert.ThrowsAsync<BookPageTranslationValidationException>(() =>
            service.TranslatePageAsync(
                document.Id,
                1,
                "owner",
                "Swedish",
                [new(1, 0, "Index must start at zero.")]));
        await Assert.ThrowsAsync<BookPageTranslationUnavailableException>(() =>
            service.TranslatePageAsync(document.Id, 1, "owner", "Swedish", []));
        await Assert.ThrowsAsync<BookPageTranslationValidationException>(() =>
            service.TranslatePageAsync(document.Id, 1, "owner", "French", SourceSegments));
        await Assert.ThrowsAsync<BookPageTranslationValidationException>(() =>
            service.TranslatePageAsync(
                document.Id,
                1,
                "owner",
                "Swedish",
                [new(0, 0, new string('x', BookPageTranslationService.MaxSegmentCharacters + 1))]));

        Assert.Equal(0, client.CallCount);
        Assert.Empty(await context.BookPageTranslations.ToListAsync());
    }

    [Fact]
    public async Task Incomplete_or_failed_generation_is_not_cached()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var incompleteClient = new FakeGenerativeAiClient
        {
            ResponseFactory = _ => new BookPageTranslationAiResponse
            {
                DetectedSourceLanguage = "English",
                Segments = [new() { Index = 0, Translation = "Only one" }],
            },
        };
        var incompleteService = CreateService(context, incompleteClient);

        await Assert.ThrowsAsync<GenerativeAiStructuredOutputException>(() =>
            incompleteService.TranslatePageAsync(
                document.Id, 1, "owner", "Swedish", SourceSegments));
        Assert.Empty(await context.BookPageTranslations.ToListAsync());

        var failingClient = new FakeGenerativeAiClient
        {
            Error = new GenerativeAiDependencyUnavailableException("Provider unavailable."),
        };
        var failingService = CreateService(context, failingClient);
        await Assert.ThrowsAsync<GenerativeAiDependencyUnavailableException>(() =>
            failingService.TranslatePageAsync(
                document.Id, 1, "owner", "German", SourceSegments));
        Assert.Empty(await context.BookPageTranslations.ToListAsync());

        var exhaustedClient = new FakeGenerativeAiClient
        {
            Error = new InsufficientAiCreditsException(0, 1),
        };
        var exhaustedService = CreateService(context, exhaustedClient);
        await Assert.ThrowsAsync<InsufficientAiCreditsException>(() =>
            exhaustedService.TranslatePageAsync(
                document.Id, 1, "owner", "Ukrainian", SourceSegments));
        Assert.Empty(await context.BookPageTranslations.ToListAsync());
    }

    [Fact]
    public async Task Invalid_translation_output_retries_gpt_then_uses_configured_fallback()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var client = new FakeGenerativeAiClient
        {
            PromptResponseFactory = (call, _) => call <= 2
                ? new BookPageTranslationAiResponse
                {
                    DetectedSourceLanguage = "English",
                    Segments = [new() { Index = 0, Translation = "Incomplete" }],
                }
                : new BookPageTranslationAiResponse
                {
                    DetectedSourceLanguage = "English",
                    Segments =
                    [
                        new() { Index = 0, Translation = "Hej där." },
                        new() { Index = 1, Translation = "Hur mår du?" },
                    ],
                },
        };

        var result = await CreateService(context, client).TranslatePageAsync(
            document.Id, 1, "owner", "Swedish", SourceSegments);

        Assert.Equal(3, client.CallCount);
        Assert.Equal(
            ["gpt-5.4-mini", "gpt-5.4-mini", "grok-4-1-fast-non-reasoning"],
            client.Models.Cast<string>());
        Assert.Equal("Hej där.", result.Segments[0].Translation);
        Assert.Equal(
            "grok-4-1-fast-non-reasoning",
            Assert.Single(await context.BookPageTranslations.ToListAsync()).Model);
    }

    [Fact]
    public async Task Unchanged_foreign_language_sentence_is_repaired_before_caching()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var client = new FakeGenerativeAiClient
        {
            ResponseFactory = call => call == 1
                ? new BookPageTranslationAiResponse
                {
                    DetectedSourceLanguage = "Polish",
                    Segments =
                    [
                        new() { Index = 0, Translation = SourceSegments[0].SourceText },
                        new() { Index = 1, Translation = "How are you in English?" },
                    ],
                }
                : new BookPageTranslationAiResponse
                {
                    DetectedSourceLanguage = "Polish",
                    Segments =
                [
                    new() { Index = 0, Translation = "Hello there in English." },
                ],
                },
        };

        var result = await CreateService(context, client).TranslatePageAsync(
            document.Id, 1, "owner", "English", SourceSegments);

        Assert.Equal(2, client.CallCount);
        Assert.Equal("Hello there in English.", result.Segments[0].Translation);
        Assert.Equal("How are you in English?", result.Segments[1].Translation);
        Assert.Single(await context.BookPageTranslations.ToListAsync());
        Assert.Equal(
            ["translate_book_page", "repair_book_page_translation"],
            client.UsageContexts.Select(context => context.Operation));
    }

    [Fact]
    public async Task Preferred_language_persists_only_for_owner()
    {
        await using var context = CreateContext();
        var (document, _) = await SeedBookAsync(context, "owner");
        var service = CreateService(context, new FakeGenerativeAiClient());

        var language = await service.UpdatePreferredLanguageAsync(
            document.Id, "owner", "ukrainian");
        context.ChangeTracker.Clear();

        Assert.Equal("Ukrainian", language);
        Assert.Equal(
            "Ukrainian",
            (await context.BookDocuments.SingleAsync()).PreferredTranslationLanguage);
        await Assert.ThrowsAsync<BookPageTranslationNotFoundException>(() =>
            service.UpdatePreferredLanguageAsync(document.Id, "other-user", "English"));
    }

    [Fact]
    public void Translation_cache_model_has_unique_key_and_cascade_delete()
    {
        using var context = CreateContext();
        var entity = context.Model.FindEntityType(typeof(BookPageTranslation));
        Assert.NotNull(entity);
        Assert.Contains(entity!.GetIndexes(), index =>
            index.IsUnique
            && index.Properties.Select(property => property.Name).SequenceEqual(
                ["BookPageId", "TargetLanguage", "SourceTextHash", "SchemaVersion"]));
        Assert.Equal(DeleteBehavior.Cascade, Assert.Single(entity.GetForeignKeys()).DeleteBehavior);
    }

    [Fact]
    public async Task Deleting_book_cascades_to_page_translation_cache()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection)
            .Options;
        await using var context = new GlosifyContext(options);
        await context.Database.EnsureCreatedAsync();
        context.Users.Add(new ApplicationUser
        {
            Id = "owner",
            UserName = "owner@example.test",
            NormalizedUserName = "OWNER@EXAMPLE.TEST",
        });
        var (document, page) = await SeedBookAsync(context, "owner");
        context.BookPageTranslations.Add(new BookPageTranslation
        {
            Id = Guid.NewGuid(),
            BookPageId = page.Id,
            TargetLanguage = "Swedish",
            SourceTextHash = new string('A', 64),
            SchemaVersion = 1,
            Model = "test-model",
            SegmentsJson = "[]",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        context.BookDocuments.Remove(document);
        await context.SaveChangesAsync();

        Assert.Empty(await context.BookPageTranslations.ToListAsync());
    }

    private static readonly Guid KnownDocumentId = Guid.Parse("c20cdf0b-76d1-461f-84d0-c09dd60916fb");

    private static GlosifyContext CreateContext(
        string? databaseName = null,
        InMemoryDatabaseRoot? root = null)
    {
        var builder = new DbContextOptionsBuilder<GlosifyContext>();
        if (root is null)
        {
            builder.UseInMemoryDatabase(databaseName ?? Guid.NewGuid().ToString("N"));
        }
        else
        {
            builder.UseInMemoryDatabase(databaseName!, root);
        }

        return new GlosifyContext(builder.Options);
    }

    private static async Task<(BookDocument Document, BookPage Page)> SeedBookAsync(
        GlosifyContext context,
        string userId,
        Guid? documentId = null)
    {
        var now = DateTimeOffset.UtcNow;
        var document = new BookDocument
        {
            Id = documentId ?? Guid.NewGuid(),
            UserId = userId,
            Title = "Test book",
            OriginalFileName = "book.pdf",
            BlobName = "books/book.pdf",
            PageCount = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        var page = new BookPage
        {
            Id = Guid.NewGuid(),
            BookDocumentId = document.Id,
            BookDocument = document,
            PageNumber = 1,
            Text = "Hello there. How are you?",
        };
        document.Pages.Add(page);
        context.BookDocuments.Add(document);
        await context.SaveChangesAsync();
        return (document, page);
    }

    private static BookPageTranslationService CreateService(
        GlosifyContext context,
        IGenerativeAiClient client,
        IBookPageTranslationCoordinator? coordinator = null,
        string provider = GenerativeAiOptions.FoundryProvider) =>
        new(
            context,
            client,
            coordinator ?? new BookPageTranslationCoordinator(),
            Options.Create(new GenerativeAiOptions
            {
                Provider = provider,
                Foundry = new FoundryGenerativeAiOptions
                {
                    StructuredDeployment = "gpt-5.4-mini",
                    PageTranslationDeployment = "gpt-5.4-mini",
                    PageTranslationFallbackDeployment = "grok-4-1-fast-non-reasoning",
                },
            }),
            Options.Create(new GeminiOptions
            {
                PageTranslationModel = "gemini-3.1-flash-lite",
            }),
            TimeProvider.System);

    private sealed class FakeGenerativeAiClient : IGenerativeAiClient
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);
        public ConcurrentQueue<AiUsageContext> UsageContexts { get; } = new();
        public ConcurrentQueue<string?> Models { get; } = new();
        public Func<int, BookPageTranslationAiResponse> ResponseFactory { get; set; } = call =>
            new BookPageTranslationAiResponse
            {
                DetectedSourceLanguage = "English",
                Segments =
                [
                    new() { Index = 0, Translation = $"Swedish translation {call - 1}" },
                    new() { Index = 1, Translation = $"Swedish translation {call}" },
                ],
            };
        public Func<int, string, BookPageTranslationAiResponse>? PromptResponseFactory { get; set; }
        public Exception? Error { get; set; }
        public bool BlockFirstCall { get; set; }
        public TaskCompletionSource FirstCallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            var call = Interlocked.Increment(ref _callCount);
            UsageContexts.Enqueue(usageContext);
            Models.Enqueue(model);
            if (BlockFirstCall && call == 1)
            {
                FirstCallEntered.TrySetResult();
                await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
            }

            if (Error is not null)
            {
                throw Error;
            }

            return (T)(object)(PromptResponseFactory?.Invoke(call, prompt) ?? ResponseFactory(call));
        }

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromException<string>(new NotSupportedException());

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromException<AgentTurnResult>(new NotSupportedException());
    }
}
