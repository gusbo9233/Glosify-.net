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
        var orchestrator = CreateOrchestrator(context, gemini: new StaticGeminiClient("Queued those words."));
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
        var gemini = new CapturingGeminiClient("Queued a quiz from the page.");
        var orchestrator = CreateOrchestrator(
            context,
            gemini: gemini,
            books: new StaticBookDocumentService(page));

        await orchestrator.SendGlobalMessageAsync(
            "user-1",
            "Make a quiz from this page",
            documentContext: new AssistantDocumentContext(documentId, 3));

        Assert.NotNull(gemini.LastAgentRequest);
        Assert.Contains("Current book page context", gemini.LastAgentRequest.SystemInstruction);
        Assert.Contains("Page: 3", gemini.LastAgentRequest.SystemInstruction);
        Assert.Contains("Pan Tadeusz opens with a longing for Lithuania.", gemini.LastAgentRequest.SystemInstruction);
    }

    [Fact]
    public async Task SendChatMessage_InstructsModelToExtractEveryNonNameWord()
    {
        await using var context = CreateContext();
        var quizId = Guid.NewGuid();
        context.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await context.SaveChangesAsync();
        var gemini = new CapturingGeminiClient("Queued the words.");
        var orchestrator = CreateOrchestrator(context, gemini: gemini);
        var chat = await orchestrator.CreateChatAsync("user-1", quizId);

        await orchestrator.SendChatMessageAsync(
            chat.Id,
            "user-1",
            "Extract vocabulary from this text.",
            contextQuizId: quizId);

        Assert.NotNull(gemini.LastAgentRequest);
        Assert.Contains(
            "extract every unique word except proper names",
            gemini.LastAgentRequest.SystemInstruction);
        Assert.Contains(
            "closed-class and basic words by default",
            gemini.LastAgentRequest.SystemInstruction);
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

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static AssistantOrchestrator CreateOrchestrator(
        GlosifyContext context,
        IGeminiClient? gemini = null,
        IChangeApplier? applier = null,
        IBookDocumentService? books = null)
    {
        return new AssistantOrchestrator(
            context,
            gemini ?? new StaticGeminiClient("Done."),
            Options.Create(new GeminiOptions { AssistantModel = "test-model", StructuredModel = "test-model" }),
            new NoopAssistantTools(),
            applier ?? new CapturingChangeApplier(),
            books ?? new NoopBookDocumentService(),
            new StaticLanguageContext(),
            NullLogger<AssistantOrchestrator>.Instance);
    }

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

    private sealed class StaticGeminiClient(string text) : IGeminiClient
    {
        public Task<string> GenerateJsonAsync(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AgentTurnResult(text, []));
    }

    private sealed class CapturingGeminiClient(string text) : IGeminiClient
    {
        public AgentRequest? LastAgentRequest { get; private set; }

        public Task<string> GenerateJsonAsync(string prompt, AiUsageContext usageContext, string? model = null, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");

        public Task<string> ExtractTextFromImageAsync(byte[] imageBytes, string contentType, string prompt, AiUsageContext usageContext, CancellationToken cancellationToken = default) =>
            Task.FromResult(string.Empty);

        public Task<AgentTurnResult> RunAgentTurnAsync(AgentRequest request, AiUsageContext usageContext, CancellationToken cancellationToken = default)
        {
            LastAgentRequest = request;
            return Task.FromResult(new AgentTurnResult(text, []));
        }
    }

    private sealed class NoopAssistantTools : IAssistantTools
    {
        public IReadOnlyList<AgentToolDeclaration> Declarations { get; } = [];
        public IReadOnlyList<AgentToolDeclaration> GlobalDeclarations { get; } = [];

        public Task<object> ExecuteAsync(string name, string argsJson, AgentToolContext context, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
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
