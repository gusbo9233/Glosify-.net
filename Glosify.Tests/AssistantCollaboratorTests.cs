using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
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
