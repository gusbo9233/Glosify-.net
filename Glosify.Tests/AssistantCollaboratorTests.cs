using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantCollaboratorTests
{
    [Fact]
    public void Presenter_normalizes_titles_and_hides_tool_payloads()
    {
        var presenter = new AssistantMessagePresenter();
        var message = new AssistantMessage
        {
            ContentJson = """{"parts":[{"kind":"functionCall","text":"internal"}]}""",
        };

        Assert.Equal("A useful title", presenter.NormalizeTitle("  A   useful  title "));
        Assert.Equal("New chat", presenter.NormalizeTitle("  "));
        Assert.Equal(string.Empty, presenter.ExtractVisibleText(message));
    }

    [Fact]
    public void Presenter_tolerates_a_missing_pending_change_payload()
    {
        var view = new AssistantMessagePresenter().PresentPendingChange(
            new PendingChange(PendingChangeKinds.AddWord, default),
            new Dictionary<string, AssistantWordLabel>());

        Assert.Equal("{}", view.PayloadJson);
        Assert.Equal(PendingChangeKinds.AddWord, view.Summary);
    }

    [Fact]
    public void Prompt_builder_composes_language_context_without_services()
    {
        var instruction = new AssistantPromptBuilder().BuildSystemInstruction(
            quiz: null,
            focusedWord: null,
            documentPage: null,
            customQuiz: null,
            transcript: null,
            book: null,
            currentLanguage: "Polish");

        Assert.Contains("Polish", instruction);
        Assert.Contains("language-learning assistant", instruction);
    }

    [Fact]
    public void Prompt_builder_preserves_exact_instruction_text()
    {
        var quiz = new Quiz
        {
            Id = Guid.Parse("11111111-1111-1111-1111-111111111111"),
            Name = "Starter quiz",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
        };
        var focusedWord = new Word
        {
            Id = "word-1",
            QuizId = quiz.Id,
            Lemma = "dom",
            Translation = "house",
        };
        var customQuiz = new CustomQuiz
        {
            Id = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            QuizId = quiz.Id,
            Name = "Chapter drill",
        };
        var document = new DocumentPageContext("Course book", 7, "Ala ma kota.", null);
        var transcript = new TranscriptAssistantContext(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Lesson recording",
            "pl",
            "source");
        var book = new BookAssistantContext(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Course book",
            42);
        var builder = new AssistantPromptBuilder();

        var outputs = new[]
        {
            builder.BuildSystemInstruction(null, null, document, null, transcript, book, "Polish"),
            builder.BuildSystemInstruction(quiz, focusedWord, document, customQuiz, transcript, book, "Polish"),
            builder.BuildProfileContext(AssistantAgentProfile.CustomQuizBuilder, quiz, focusedWord, document, customQuiz, transcript, book, "Polish"),
            builder.BuildProfileContext(AssistantAgentProfile.QuizAssistant, quiz, focusedWord, document, customQuiz, transcript, book, "Polish"),
            builder.BuildProfileContext(AssistantAgentProfile.Librarian, null, null, document, null, transcript, book, "Polish"),
        };

        Assert.Equal(
            new[]
            {
                "36D23F3A5F9189D7EB9020C390B9B5791ED3AF2C0020FCAD2C0430E26AB00EEA",
                "951D227C139F9A42A70FB9AA9F14ECE928A29DA6C222ED3921E0EFD1965CCEB3",
                "94C2DBA88F160B2FBEB785E1B4BA929BA3D3670FFA1F8F5268098DDCCEC68324",
                "5939496F9AF4B8FC4A36314E828A156AC577A72CDFACEAF5E296CB7CBF1394C3",
                "23A2B9DF6982AD52D9A0112B1381DBBC187A141BE9AA42361B43EA85C1AC0E0E",
            },
            outputs.Select(Fingerprint));
    }

    [Fact]
    public async Task Context_resolver_enforces_quiz_ownership_and_prefers_request_language()
    {
        await using var context = CreateContext();
        var ownedQuiz = new Quiz
        {
            Id = Guid.NewGuid(),
            UserId = "owner",
            Name = "Owned",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
        };
        context.Quizzes.Add(ownedQuiz);
        await context.SaveChangesAsync();

        var resolver = new AssistantContextResolver(
            context,
            new NoopBookService(),
            new FixedLanguageContext("German"),
            new FixedLanguagePreference());

        Assert.Equal("German", await resolver.ResolveLanguageAsync("owner", CancellationToken.None));
        Assert.Equal(ownedQuiz.Id, (await resolver.ResolveQuizAsync(ownedQuiz.Id, "owner", CancellationToken.None))?.Id);
        await Assert.ThrowsAsync<QuizNotFoundException>(() =>
            resolver.ResolveQuizAsync(ownedQuiz.Id, "another-user", CancellationToken.None));
    }

    private static GlosifyContext CreateContext() => new(
        new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private static string Fingerprint(string value)
    {
        var normalized = value.Replace("\r\n", "\n", StringComparison.Ordinal);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private sealed class FixedLanguageContext(string language) : ILanguageContext
    {
        public string? CurrentLanguage => language;
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages => [language];
        public bool TrySetLanguage(string value) => false;
        public void Clear() { }
    }

    private sealed class FixedLanguagePreference : IQuizLanguagePreferenceService
    {
        public Task<QuizLanguage?> GetSelectedAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult<QuizLanguage?>(new QuizLanguage("pl", "Polish"));

        public Task<QuizLanguage> SetSelectedAsync(string userId, string language, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ClearAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class NoopBookService : IBookDocumentService
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
        public Task<bool> DeleteAsync(Guid documentId, string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
        public Task<Stream> OpenPdfUncheckedAsync(Guid documentId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
