using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services;
using Glosify.Services.Books;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Ai.Llm;
using Glosify.Services.Language;

namespace Glosify.Tests;

public class AssistantSavedChatsTests
{
    [Fact]
    public async Task CreateChat_StoresGlobalThreadWithContext()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Null(thread.QuizId);
        Assert.Equal(quizId, thread.ContextQuizId);
        Assert.Equal("New chat", chat.Title);
    }

    [Fact]
    public async Task ListChats_ReturnsOnlyCurrentUsersGlobalChats()
    {
        await using var context = CreateContext();
        context.AssistantThreads.AddRange(
            CreateThread("user-1", title: "Mine"),
            CreateThread("user-2", title: "Other user"),
            CreateThread("user-1", title: "Legacy quiz thread", quizId: Guid.NewGuid()));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        var chats = await orchestrator.ListChatsAsync("user-1");

        var chat = Assert.Single(chats);
        Assert.Equal("Mine", chat.Title);
    }

    [Fact]
    public async Task SendChatMessage_AutoTitlesAndPersistsMessages()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, generativeAi: new StaticGenerativeAiClient("Queued those words."));
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        var response = await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Create a Polish verbs quiz",
            contextQuizId: quizId);

        var thread = await context.AssistantThreads.SingleAsync(t => t.Id == chat.Id);
        Assert.Equal("Create a Polish verbs quiz", thread.Title);
        Assert.Equal(quizId, thread.ContextQuizId);
        Assert.Equal(chat.Id, response.ThreadId);
        Assert.Equal(2, await context.AssistantMessages.CountAsync(m => m.ThreadId == chat.Id));
    }

    [Fact]
    public async Task SendGlobalMessage_IncludesBookPageContext()
    {
        await using var context = CreateContext();
        var documentId = Guid.NewGuid();
        var page = CreateBookPage(documentId, "user-1", "Pan Tadeusz opens with a longing for Lithuania.");
        var generativeAi = new CapturingGenerativeAiClient("Queued a quiz from the page.");
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            books: new StaticBookDocumentService(page));

        await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Make a quiz from this page",
            documentContext: new AssistantDocumentContext(documentId, 3));

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains("Current book page context", generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains("Page: 3", generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains("Pan Tadeusz opens with a longing for Lithuania.", generativeAi.LastAgentRequest.SystemInstruction);
    }

    [Fact]
    public async Task SendChatMessage_InstructsModelToExtractEveryNonNameWord()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var generativeAi = new CapturingGenerativeAiClient("Queued the words.");
        var orchestrator = CreateOrchestrator(context, generativeAi: generativeAi);
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Extract vocabulary from this text.",
            contextQuizId: quizId);

        Assert.NotNull(generativeAi.LastAgentRequest);
        Assert.Contains(
            "every unique word except proper names",
            generativeAi.LastAgentRequest.SystemInstruction);
        Assert.Contains(
            "including closed-class words",
            generativeAi.LastAgentRequest.SystemInstruction);
    }

    [Fact]
    public async Task ApplyPendingChanges_UsesSavedMessageContextQuiz()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(new AssistantMessage
        {
            Id = messageId,
            ThreadId = thread.Id,
            ContextQuizId = quizId,
            Sequence = 0,
            Role = AssistantMessageRole.Model,
            ContentJson = StoredText("Ready."),
            PendingChangesJson = JsonSerializer.Serialize(new[]
            {
                new PendingChange(PendingChangeKinds.AddWord, JsonSerializer.SerializeToElement(new
                {
                    word = "iść",
                    translation = "to go",
                })),
            }),
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var applier = new CapturingChangeApplier();
        var orchestrator = CreateOrchestrator(context, applier: applier);

        var result = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");

        Assert.Equal(1, result.Applied);
        Assert.Equal(quizId, applier.QuizId);
        Assert.Equal(AssistantMessageStatus.Applied, (await context.AssistantMessages.SingleAsync(m => m.Id == messageId)).Status);
    }

    [Fact]
    public async Task ApplyPendingChanges_SecondApplyIsANoOp()
    {
        await using var context = CreateContext();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await context.SaveChangesAsync();
        var applier = new CountingChangeApplier();
        var orchestrator = CreateOrchestrator(context, applier: applier);

        var first = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");
        var second = await orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1");

        Assert.Equal(1, first.Applied);
        Assert.Equal(0, second.Applied);
        Assert.Equal(1, applier.Calls);
    }

    [Fact]
    public async Task ApplyPendingChanges_RevertsClaimWhenApplyFails()
    {
        await using var context = CreateContext();
        var messageId = Guid.NewGuid();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(CreateActiveMessageWithPendingChange(messageId, thread.Id));
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context, applier: new ThrowingChangeApplier());

        await Assert.ThrowsAsync<InvalidDataException>(
            () => orchestrator.ApplyGlobalPendingChangesAsync(messageId, "user-1"));

        Assert.Equal(
            AssistantMessageStatus.Active,
            (await context.AssistantMessages.SingleAsync(m => m.Id == messageId)).Status);
    }

    [Fact]
    public async Task DeleteChat_RemovesMessagesAndBlocksLaterHistory()
    {
        await using var context = CreateContext();
        var thread = CreateThread("user-1");
        context.AssistantThreads.Add(thread);
        context.AssistantMessages.Add(new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Sequence = 0,
            Role = AssistantMessageRole.User,
            ContentJson = StoredText("Hello"),
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();
        var orchestrator = CreateOrchestrator(context);

        await orchestrator.DeleteChatAsync(thread.Id, "user-1");

        Assert.Empty(context.AssistantMessages);
        await Assert.ThrowsAsync<InvalidOperationException>(() => orchestrator.GetChatHistoryAsync(thread.Id, "user-1"));
    }

    [Fact]
    public async Task Assistant_stops_after_twenty_four_model_tool_turns()
    {
        await using var context = CreateContext();
        var generativeAi = new LoopingGenerativeAiClient();
        var tools = new LoopAssistantTools();
        var orchestrator = CreateOrchestrator(
            context,
            generativeAi: generativeAi,
            tools: tools);

        var result = await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Keep looking things up.");

        Assert.Equal(24, generativeAi.Calls);
        Assert.Equal(24, tools.Calls);
        Assert.Contains("tool-call limit", result.AssistantText);
        Assert.Equal(
            50,
            await context.AssistantMessages.CountAsync(message => message.ThreadId == result.ThreadId));
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static AssistantOrchestrator CreateOrchestrator(
        GlosifyContext context,
        IGenerativeAiClient? generativeAi = null,
        IChangeApplier? applier = null,
        IBookDocumentService? books = null,
        IAssistantTools? tools = null)
    {
        return new AssistantOrchestrator(
            context,
            generativeAi ?? new StaticGenerativeAiClient("Done."),
            CreateModelResolver(),
            tools ?? new NoopAssistantTools(),
            applier ?? new CapturingChangeApplier(),
            books ?? new NoopBookDocumentService(),
            new StaticLanguageContext(),
            NullLogger<AssistantOrchestrator>.Instance);
    }

    private static IGenerativeAiModelResolver CreateModelResolver() =>
        new GenerativeAiModelResolver(
            Options.Create(new GenerativeAiOptions
            {
                Provider = GenerativeAiOptions.FoundryProvider,
                Foundry = new FoundryGenerativeAiOptions
                {
                    ProjectEndpoint = "https://example.services.ai.azure.com/api/projects/test",
                    AssistantDeployment = "test-model",
                    StructuredDeployment = "test-model",
                    VisionDeployment = "test-model",
                    AllowedAssistantDeployments = ["test-model"],
                    AssistantModels =
                    [
                        new AssistantModelOptions
                        {
                            Deployment = "test-model",
                            DisplayName = "Test Model",
                            Provider = "Test",
                            SpeedTier = "Test",
                            CostTier = "Test",
                            CreditMultiplier = 1m,
                        },
                    ],
                },
            }),
            Options.Create(new GeminiOptions
            {
                Model = "test-model",
                AssistantModel = "test-model",
                StructuredModel = "test-model",
            }));

    private static Quiz CreateQuiz(Guid id, string userId) => new()
    {
        Id = id,
        UserId = userId,
        Name = "Polish",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private static BookPage CreateBookPage(Guid documentId, string userId, string text)
    {
        var document = new BookDocument
        {
            Id = documentId,
            UserId = userId,
            Title = "Polish Reader",
            OriginalFileName = "polish-reader.pdf",
            BlobName = "books/polish-reader.pdf",
            PageCount = 5,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

        return new BookPage
        {
            Id = Guid.NewGuid(),
            BookDocumentId = documentId,
            PageNumber = 3,
            Text = text,
            BookDocument = document,
        };
    }

    private static AssistantThread CreateThread(string userId, string title = "New chat", Guid? quizId = null) => new()
    {
        Id = Guid.NewGuid(),
        QuizId = quizId,
        UserId = userId,
        Title = title,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static string StoredText(string text) =>
        JsonSerializer.Serialize(new
        {
            parts = new[]
            {
                new { kind = "text", text },
            },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

    private sealed class StaticGenerativeAiClient(string text) : IGenerativeAiClient
    {
        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentTurnResult(text, []));
    }

    private sealed class CapturingGenerativeAiClient(string text) : IGenerativeAiClient
    {
        public AgentRequest? LastAgentRequest { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default)
        {
            LastAgentRequest = request;
            return Task.FromResult(new AgentTurnResult(text, []));
        }
    }

    private sealed class LoopingGenerativeAiClient : IGenerativeAiClient
    {
        public int Calls { get; private set; }

        public Task<T> GenerateStructuredAsync<T>(
            string prompt,
            AiUsageContext usageContext,
            string? model = null,
            CancellationToken cancellationToken = default) =>
            Task.FromException<T>(new NotSupportedException());

        public Task<string> ExtractTextFromImageAsync(
            byte[] imageBytes,
            string contentType,
            string prompt,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(
            AgentRequest request,
            AiUsageContext usageContext,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new AgentTurnResult(
                string.Empty,
                [
                    new AgentFunctionCall("loop", "{}")
                    {
                        CallId = $"call-{Calls}",
                    },
                ]));
        }
    }

    private sealed class NoopAssistantTools : IAssistantTools
    {
        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } = [];

        public Task<object> ExecuteAsync(string name, string argsJson, AgentToolContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class LoopAssistantTools : IAssistantTools
    {
        public int Calls { get; private set; }
        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } =
        [
            new AgentToolDeclaration(
                "loop",
                "Continues the test loop.",
                new { type = "object", properties = new { } }),
        ];

        public Task<object> ExecuteAsync(
            string name,
            string argsJson,
            AgentToolContext context,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<object>(new { ok = true });
        }
    }

    private sealed class CapturingChangeApplier : IChangeApplier
    {
        public Guid? QuizId { get; private set; }

        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
        {
            QuizId = quizId;
            return Task.FromResult(new AssistantApplyResult(changes.Count));
        }
    }

    private sealed class CountingChangeApplier : IChangeApplier
    {
        public int Calls { get; private set; }

        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AssistantApplyResult(changes.Count));
        }
    }

    private sealed class ThrowingChangeApplier : IChangeApplier
    {
        public Task<AssistantApplyResult> ApplyAsync(Guid? quizId, string userId, IReadOnlyList<PendingChange> changes, CancellationToken cancellationToken) =>
            throw new InvalidDataException("Simulated apply failure.");
    }

    private static AssistantMessage CreateActiveMessageWithPendingChange(Guid messageId, Guid threadId) => new()
    {
        Id = messageId,
        ThreadId = threadId,
        Sequence = 0,
        Role = AssistantMessageRole.Model,
        ContentJson = StoredText("Ready."),
        PendingChangesJson = JsonSerializer.Serialize(new[]
        {
            new PendingChange(PendingChangeKinds.CreateCollection, JsonSerializer.SerializeToElement(new
            {
                name = "Food",
                language = "Polish",
            })),
        }),
        Status = AssistantMessageStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
    };

    private sealed class NoopBookDocumentService : IBookDocumentService
    {
        public Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookDocument>>([]);

        public Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookDocument?>(null);

        public Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookPage?>(null);

        public Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticBookDocumentService(BookPage page) : IBookDocumentService
    {
        public Task<IReadOnlyList<BookDocument>> GetUserBooksAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BookDocument>>([page.BookDocument]);

        public Task<BookDocument> UploadAsync(string userId, IFormFile file, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<BookDocument?> GetOwnedDocumentAsync(Guid id, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookDocument?>(page.BookDocument.Id == id && page.BookDocument.UserId == userId ? page.BookDocument : null);

        public Task<BookPage?> GetOwnedPageAsync(Guid documentId, int pageNumber, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BookPage?>(
                page.BookDocumentId == documentId && page.PageNumber == pageNumber && page.BookDocument.UserId == userId
                    ? page
                    : null);

        public Task<Stream> OpenOwnedPdfAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class StaticLanguageContext : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];

        public bool TrySetLanguage(string language) => true;

        public void Clear()
        {
        }
    }
}
