using System.Text.Json;
using Glosify.Data;
using Glosify.Infrastructure.Concurrency;
using Glosify.Models.Entities;
using Glosify.Models.QuizImports;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Quizzes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizJsonImportServiceTests
{
    [Fact]
    public async Task FreestyleImport_ForcesDurableModeValuesAndRejectsSentences()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var json = """
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [{
                "name": "Cardiology",
                "source_language": "Polish",
                "words": [{ "word": "What is preload?", "translation": "Ventricular stretch before contraction." }],
                "sentences": []
              }],
              "collections": []
            }
            """;

        var preview = await service.PreviewAsync(json, "free", null, "user-1");
        await service.ApplyAsync(preview.CanonicalJson, "free", null, "user-1");

        var quiz = await db.Quizzes.SingleAsync();
        Assert.Equal("Freestyle", quiz.SourceLanguage);
        Assert.Equal("Freestyle", quiz.TargetLanguage);
        Assert.Equal("Freestyle", quiz.Language);
        Assert.Equal("What is preload?", (await db.Words.SingleAsync()).Lemma);

        var sentenceError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(
                """{"version":1,"source_language":"Freestyle","quizzes":[{"name":"Bad","words":[],"sentences":[{"text":"Not offered","translation":"No"}]}],"collections":[]}""",
                "Freestyle",
                null,
                "user-1"));
        Assert.Contains("$.quizzes[0].sentences", sentenceError.Errors.Keys);
    }

    [Fact]
    public async Task Preview_repairs_safe_wrappers_and_reports_deduplicated_content()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var json = """
            The model returned this:
            ```json
            {
              // One shared translation language.
              "version": 1,
              "source_language": " English ",
              "quizzes": [{
                "name": " Basics ",
                "words": [
                  { "word": "dom", "translation": "house" },
                  { "word": "DOM", "translation": "home" },
                  { "word": "To jest dom.", "translation": "This is a house." },
                ],
                "sentences": [
                  { "text": "To jest dom.", "translation": "This is a house." },
                ],
              }],
              "collections": [],
            }
            ```
            """;

        var preview = await service.PreviewAsync(json, "Polish", null, "user-1");

        Assert.True(preview.WasAutoRepaired);
        Assert.Equal(new QuizJsonImportTotals(0, 1, 1, 1), preview.Totals);
        Assert.Equal(2, preview.Warnings.Count);
        var quiz = Assert.Single(preview.Quizzes);
        Assert.Equal("Basics", quiz.Name);
        Assert.Equal("English", quiz.SourceLanguage);
        Assert.DoesNotContain("```", preview.CanonicalJson);
        Assert.DoesNotContain("//", preview.CanonicalJson);
        Assert.Contains("\"version\": 1", preview.CanonicalJson);
    }

    [Fact]
    public async Task Preview_rejects_unknown_fields_and_unsupported_versions_with_json_paths()
    {
        await using var db = CreateContext();
        var service = CreateService(db);

        var unknown = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(
                """{"version":1,"source_language":"English","target_language":"Polish","quizzes":[],"collections":[]}""",
                "Polish",
                null,
                "user-1"));
        Assert.Contains(unknown.Errors.Keys, path => path.Contains("target_language", StringComparison.Ordinal));

        var unsupported = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(
                """{"version":2,"source_language":"English","quizzes":[],"collections":[{"name":"Empty"}]}""",
                "Polish",
                null,
                "user-1"));
        Assert.Contains("$.version", unsupported.Errors.Keys);
        Assert.NotNull(unsupported.CanonicalJson);
    }

    [Fact]
    public async Task Preview_enforces_per_quiz_item_limit_before_deduplication()
    {
        await using var db = CreateContext();
        var service = CreateService(db);
        var words = Enumerable.Range(0, 101)
            .Select(index => new { word = $"word-{index}", translation = $"translation-{index}" });
        var json = JsonSerializer.Serialize(new
        {
            version = 1,
            source_language = "English",
            quizzes = new[] { new { name = "Too large", words, sentences = Array.Empty<object>() } },
            collections = Array.Empty<object>(),
        });

        var exception = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(json, "Polish", null, "user-1"));

        Assert.Contains(exception.Errors.SelectMany(pair => pair.Value), message => message.Contains("at most 100", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_rejects_unrepairable_syntax_and_all_hierarchy_limits()
    {
        await using var db = CreateContext();
        var service = CreateService(db);

        var malformed = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync("{\"version\": 1, source_language", "Polish", null, "user-1"));
        Assert.Contains("$", malformed.Errors.Keys);

        var tooManyCollections = JsonSerializer.Serialize(new QuizJsonImportDocumentV1
        {
            Version = 1,
            SourceLanguage = "English",
            Collections = Enumerable.Range(0, 26)
                .Select(index => new QuizJsonImportCollectionV1 { Name = $"Collection {index}" })
                .ToList(),
        });
        var collectionError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(tooManyCollections, "Polish", null, "user-1"));
        Assert.Contains(collectionError.Errors.SelectMany(pair => pair.Value), message => message.Contains("at most 25", StringComparison.Ordinal));

        var tooManyQuizzes = JsonSerializer.Serialize(new QuizJsonImportDocumentV1
        {
            Version = 1,
            SourceLanguage = "English",
            Quizzes = Enumerable.Range(0, 51).Select(index => QuizWithWords($"Quiz {index}", 1)).ToList(),
        });
        var quizError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(tooManyQuizzes, "Polish", null, "user-1"));
        Assert.Contains(quizError.Errors.SelectMany(pair => pair.Value), message => message.Contains("at most 50", StringComparison.Ordinal));

        var tooManyItems = JsonSerializer.Serialize(new QuizJsonImportDocumentV1
        {
            Version = 1,
            SourceLanguage = "English",
            Quizzes = Enumerable.Range(0, 11).Select(index => QuizWithWords($"Quiz {index}", 100)).ToList(),
        });
        var itemError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(tooManyItems, "Polish", null, "user-1"));
        Assert.Contains(itemError.Errors.SelectMany(pair => pair.Value), message => message.Contains("at most 1000", StringComparison.Ordinal));

        var nested = new QuizJsonImportCollectionV1 { Name = "Level 6" };
        for (var depth = 5; depth >= 1; depth--)
        {
            nested = new QuizJsonImportCollectionV1
            {
                Name = $"Level {depth}",
                Collections = [nested],
            };
        }
        var tooDeep = JsonSerializer.Serialize(new QuizJsonImportDocumentV1
        {
            Version = 1,
            SourceLanguage = "English",
            Collections = [nested],
        });
        var depthError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(tooDeep, "Polish", null, "user-1"));
        Assert.Contains(depthError.Errors.SelectMany(pair => pair.Value), message => message.Contains("at most 5", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Preview_rejects_foreign_parent_and_existing_sibling_collection()
    {
        await using var db = CreateContext();
        var foreignParent = Collection("other-user", "Polish", "Foreign");
        var existing = Collection("user-1", "Polish", "Travel");
        db.Collections.AddRange(foreignParent, existing);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        const string json = """
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [],
              "collections": [{ "name": "Travel", "quizzes": [], "collections": [] }]
            }
            """;

        var parentError = await Assert.ThrowsAsync<QuizJsonImportValidationException>(() =>
            service.PreviewAsync(json, "Polish", foreignParent.Id, "user-1"));
        Assert.Contains("$.parent_collection_id", parentError.Errors.Keys);

        await Assert.ThrowsAsync<CollectionNameConflictException>(() =>
            service.PreviewAsync(json, "Polish", null, "user-1"));

        const string duplicatedSiblings = """
            {"version":1,"source_language":"English","quizzes":[],"collections":[
              {"name":"Trips","quizzes":[],"collections":[]},
              {"name":"TRIPS","quizzes":[],"collections":[]}
            ]}
            """;
        await Assert.ThrowsAsync<CollectionNameConflictException>(() =>
            service.PreviewAsync(duplicatedSiblings, "Polish", null, "another-user"));
    }

    [Fact]
    public async Task Apply_persists_nested_private_hierarchy_with_source_overrides()
    {
        await using var db = CreateContext();
        var destination = Collection("user-1", "Polish", "Course");
        db.Collections.Add(destination);
        await db.SaveChangesAsync();
        var service = CreateService(db);
        const string json = """
            {
              "version": 1,
              "source_language": "English",
              "quizzes": [{
                "name": "Root quiz",
                "words": [{ "word": "dom", "translation": "house" }],
                "sentences": []
              }],
              "collections": [{
                "name": "Travel",
                "quizzes": [{
                  "name": "Station",
                  "source_language": "Swedish",
                  "words": [],
                  "sentences": [{ "text": "Gdzie jest pociąg?", "translation": "Var är tåget?" }]
                }],
                "collections": [{ "name": "Empty child", "quizzes": [], "collections": [] }]
              }]
            }
            """;

        var result = await service.ApplyAsync(json, "Polish", destination.Id, "user-1");

        Assert.Equal(new QuizJsonImportResult(2, 2, 1, 1), result);
        var collections = await db.Collections.OrderBy(item => item.Name).ToListAsync();
        Assert.Equal(3, collections.Count);
        var travel = Assert.Single(collections, item => item.Name == "Travel");
        Assert.Equal(destination.Id, travel.ParentCollectionId);
        Assert.False(travel.IsPublic);
        var child = Assert.Single(collections, item => item.Name == "Empty child");
        Assert.Equal(travel.Id, child.ParentCollectionId);

        var quizzes = await db.Quizzes.OrderBy(item => item.Name).ToListAsync();
        Assert.All(quizzes, quiz =>
        {
            Assert.Equal("Polish", quiz.TargetLanguage);
            Assert.Equal("Polish", quiz.Language);
            Assert.Equal("Ready", quiz.ProcessingStatus);
            Assert.False(quiz.IsPublic);
            Assert.Equal("user-1", quiz.UserId);
        });
        Assert.Equal("Swedish", Assert.Single(quizzes, quiz => quiz.Name == "Station").SourceLanguage);
        Assert.Equal("English", Assert.Single(quizzes, quiz => quiz.Name == "Root quiz").SourceLanguage);
        Assert.Single(db.Words);
        Assert.Single(db.QuizSentences);
    }

    [Fact]
    public async Task Ai_repair_is_explicitly_metered_and_then_revalidated_without_saving()
    {
        await using var db = CreateContext();
        var importService = CreateService(db);
        var ai = new RecordingAiClient
        {
            Response = new QuizJsonImportAiRepairEnvelope("""
                {
                  "version": 1,
                  "source_language": "English",
                  "quizzes": [{
                    "name": "Repaired",
                    "words": [{ "word": "dom", "translation": "house" }],
                    "sentences": []
                  }],
                  "collections": []
                }
                """),
        };
        var repair = new QuizJsonImportRepairService(importService, ai);

        var preview = await repair.RepairAsync("{ bad json", "Polish", null, "user-1");

        Assert.Equal("Repaired", Assert.Single(preview.Quizzes).Name);
        Assert.Equal(AiUsageFeatures.JsonImportRepair, ai.Usage?.Feature);
        Assert.Equal("repair_quiz_json_import", ai.Usage?.Operation);
        Assert.Empty(db.Quizzes);
        Assert.Empty(db.Words);
    }

    [Fact]
    public async Task Ai_repair_skips_the_provider_when_json_is_already_valid()
    {
        await using var db = CreateContext();
        var ai = new RecordingAiClient();
        var repair = new QuizJsonImportRepairService(CreateService(db), ai);
        const string json = """
            {"version":1,"source_language":"English","quizzes":[{"name":"Valid","words":[{"word":"dom","translation":"house"}],"sentences":[]}],"collections":[]}
            """;

        var preview = await repair.RepairAsync(json, "Polish", null, "user-1");

        Assert.Equal("Valid", Assert.Single(preview.Quizzes).Name);
        Assert.Null(ai.Usage);
    }

    [Fact]
    public async Task Ai_repair_rejects_an_invalid_provider_result_without_saving()
    {
        await using var db = CreateContext();
        var ai = new RecordingAiClient
        {
            Response = new QuizJsonImportAiRepairEnvelope("{ still invalid"),
        };
        var repair = new QuizJsonImportRepairService(CreateService(db), ai);

        var exception = await Assert.ThrowsAsync<QuizJsonImportAiUnprocessableException>(() =>
            repair.RepairAsync("{ bad json", "Polish", null, "user-1"));

        Assert.NotNull(exception.Errors);
        Assert.Contains("$", exception.Errors.Keys);
        Assert.Null(exception.CanonicalJson);
        Assert.Empty(db.Collections);
        Assert.Empty(db.Quizzes);
        Assert.Empty(db.Words);
        Assert.Empty(db.QuizSentences);
    }

    [Fact]
    public async Task Ai_repair_rejects_a_null_provider_envelope_without_saving()
    {
        await using var db = CreateContext();
        var ai = new RecordingAiClient { ReturnNull = true };
        var repair = new QuizJsonImportRepairService(CreateService(db), ai);

        await Assert.ThrowsAsync<QuizJsonImportAiUnprocessableException>(() =>
            repair.RepairAsync("{ bad json", "Polish", null, "user-1"));

        Assert.Empty(db.Collections);
        Assert.Empty(db.Quizzes);
        Assert.Empty(db.Words);
        Assert.Empty(db.QuizSentences);
    }

    [Fact]
    public async Task Apply_rolls_back_the_complete_relational_import_when_save_finishes_with_an_error()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var setupOptions = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection)
            .Options;
        await using (var setup = new GlosifyContext(setupOptions))
        {
            await setup.Database.EnsureCreatedAsync();
            setup.Users.Add(new ApplicationUser
            {
                Id = "user-1",
                UserName = "json-import@example.test",
                NormalizedUserName = "JSON-IMPORT@EXAMPLE.TEST",
                Email = "json-import@example.test",
                NormalizedEmail = "JSON-IMPORT@EXAMPLE.TEST",
            });
            await setup.SaveChangesAsync();
        }

        var failingOptions = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection)
            .AddInterceptors(new ThrowAfterSaveInterceptor())
            .Options;
        await using (var failing = new GlosifyContext(failingOptions))
        {
            var service = CreateService(failing);
            const string json = """
                {"version":1,"source_language":"English","quizzes":[
                  {"name":"Rollback","words":[{"word":"dom","translation":"house"}],"sentences":[{"text":"To jest dom.","translation":"This is a house."}]}
                ],"collections":[{"name":"Rollback collection","quizzes":[],"collections":[]}]}
                """;

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                service.ApplyAsync(json, "Polish", null, "user-1"));
        }

        await using var verification = new GlosifyContext(setupOptions);
        Assert.Empty(await verification.Collections.ToListAsync());
        Assert.Empty(await verification.Quizzes.ToListAsync());
        Assert.Empty(await verification.Words.ToListAsync());
        Assert.Empty(await verification.QuizSentences.ToListAsync());
    }

    private static QuizJsonImportService CreateService(GlosifyContext context) =>
        new(context, new ReferenceCountedKeyedAsyncLock(), TimeProvider.System);

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase($"quiz-json-import-{Guid.NewGuid():N}")
            .Options;
        return new GlosifyContext(options);
    }

    private static QuizJsonImportQuizV1 QuizWithWords(string name, int count) => new()
    {
        Name = name,
        Words = Enumerable.Range(0, count)
            .Select(index => new QuizJsonImportWordV1
            {
                Word = $"w{index}",
                Translation = "t",
            })
            .ToList(),
    };

    private static Collection Collection(string userId, string language, string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Language = language,
        Name = name,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class RecordingAiClient : IGenerativeAiClient
    {
        public object? Response { get; init; }
        public bool ReturnNull { get; init; }
        public AiUsageContext? Usage { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default) =>
            GenerateJsonAsync<T>(prompt, usageContext, model, cancellationToken);

        public Task<T> GenerateJsonAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default)
        {
            Usage = usageContext;
            if (ReturnNull)
            {
                return Task.FromResult((T)(object?)null!);
            }
            return Task.FromResult((T)(Response ?? throw new InvalidOperationException("No response configured.")));
        }

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowAfterSaveInterceptor : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<int>(new InvalidOperationException("Simulated late save failure."));
    }
}
