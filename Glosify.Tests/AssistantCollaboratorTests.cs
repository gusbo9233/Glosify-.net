using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Books;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.RealtimeTranslation;
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
            transcript: null,
            book: null,
            currentLanguage: "Polish");

        Assert.Contains("Polish", instruction);
        Assert.Contains("language-learning assistant", instruction);
    }

    /// <summary>
    /// The page contract in words, next to the hash pin above: the hash catches any drift
    /// at all, these assertions say what the prompt actually has to promise.
    /// </summary>
    [Theory]
    [InlineData(2, "source", "reading page 2 of 3 of the source stream")]
    [InlineData(1, "translation", "reading page 1 of 2 of the translation stream")]
    public void Prompt_builder_names_the_page_the_user_is_reading(
        int viewedPage,
        string viewedStream,
        string expected)
    {
        var instruction = BuildTranscriptPrompt(viewedPage, viewedStream);

        Assert.Contains("pages of 100 captions", instruction);
        Assert.Contains("Source: 3 page(s), 250 captions", instruction);
        Assert.Contains("Translation: 2 page(s), 130 captions", instruction);
        Assert.Contains(expected, instruction);
        // Offsets and page numbers are not comparable across streams; only time is.
        Assert.Contains("at_time of that passage — not its page or offset", instruction);
    }

    [Theory]
    [InlineData(0, "source")]
    [InlineData(4, "source")]
    [InlineData(3, "translation")]
    public void Prompt_builder_omits_a_page_the_user_cannot_be_reading(int viewedPage, string viewedStream)
    {
        var instruction = BuildTranscriptPrompt(viewedPage, viewedStream);

        Assert.Contains("Current saved transcript context", instruction);
        Assert.DoesNotContain("right now", instruction);
    }

    private static string BuildTranscriptPrompt(int viewedPage, string viewedStream) =>
        new AssistantPromptBuilder().BuildSystemInstruction(
            quiz: null,
            focusedWord: null,
            documentPage: null,
            transcript: new TranscriptAssistantContext(
                Guid.Parse("33333333-3333-3333-3333-333333333333"),
                "Lesson recording",
                "pl",
                "source",
                SourceSegmentCount: 250,
                TranslationSegmentCount: 130,
                ViewedPage: viewedPage,
                ViewedStream: viewedStream),
            book: null,
            currentLanguage: "Polish");

    [Fact]
    public void Prompt_builder_preserves_standard_quiz_and_language_instructions()
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
        var document = new DocumentPageContext("Course book", 7, "Ala ma kota.", null);
        var transcript = new TranscriptAssistantContext(
            Guid.Parse("33333333-3333-3333-3333-333333333333"),
            "Lesson recording",
            "pl",
            "source",
            SourceSegmentCount: 250,
            TranslationSegmentCount: 130,
            ViewedPage: 2,
            ViewedStream: "source");
        var book = new BookAssistantContext(
            Guid.Parse("44444444-4444-4444-4444-444444444444"),
            "Course book",
            42);
        var builder = new AssistantPromptBuilder();

        var system = builder.BuildSystemInstruction(quiz, focusedWord, document, transcript, book, "Polish");
        var profile = builder.BuildProfileContext(
            AssistantAgentProfile.QuizAssistant,
            quiz,
            focusedWord,
            document,
            transcript,
            book,
            "Polish",
            "English",
            "Swedish");

        Assert.Contains("standard word-and-translation", system);
        Assert.Contains("no longer available", system);
        Assert.DoesNotContain("create_custom_quiz", system);
        Assert.Contains("Reply language: Swedish", profile);
        Assert.Contains("Source/translation language: English", profile);
    }

    [Theory]
    [InlineData(AssistantAgentProfile.FreestyleQuizAssistant)]
    [InlineData(AssistantAgentProfile.FreestyleLibrarian)]
    public void Freestyle_profiles_offer_a_generic_standard_quiz_replacement(
        AssistantAgentProfile profile)
    {
        var instruction = AssistantProfileInstructions.Get(profile);

        Assert.Contains("standard prompt-and-answer quiz", instruction);
        Assert.DoesNotContain("word-and-translation", instruction);
        Assert.DoesNotContain("sentence-and-translation", instruction);
    }

    [Fact]
    public void Freestyle_prompt_does_not_narrow_standard_quizzes_to_language_pairs()
    {
        var instruction = new AssistantPromptBuilder().BuildSystemInstruction(
            quiz: null,
            focusedWord: null,
            documentPage: null,
            transcript: null,
            book: null,
            currentLanguage: "Freestyle");

        Assert.Contains("equivalent standard prompt-and-answer quiz", instruction);
        Assert.DoesNotContain("word or sentence pairs", instruction);
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

    /// <summary>
    /// The viewed page arrives from the browser, so the resolver checks it against the
    /// chosen stream's real length. An unusable page is dropped rather than clamped: a
    /// clamped page would tell the model the user is reading the last page when they are
    /// not, and "this page" would then resolve to text they never saw.
    /// </summary>
    [Theory]
    [InlineData(2, "source", 2)]
    [InlineData(2, "translation", 2)]
    [InlineData(0, "source", null)]
    [InlineData(4, "source", null)]
    // The streams have different lengths, so page 3 exists in one and not the other.
    [InlineData(3, "source", 3)]
    [InlineData(3, "translation", null)]
    public async Task Context_resolver_keeps_only_a_viewed_page_that_exists(
        int viewedPage,
        string viewedStream,
        int? expected)
    {
        await using var context = CreateContext();
        var transcriptId = Guid.NewGuid();
        AddTranscriptWithSegments(context, transcriptId, sourceSegments: 250, translationSegments: 130);
        await context.SaveChangesAsync();
        var resolver = new AssistantContextResolver(
            context,
            new NoopBookService(),
            new FixedLanguageContext("Polish"),
            new FixedLanguagePreference());

        var resolved = await resolver.ResolveTranscriptAsync(
            transcriptId,
            new AssistantTranscriptPageContext(viewedPage, viewedStream),
            "owner",
            CancellationToken.None);

        Assert.Equal(expected, resolved!.ViewedPage);
        Assert.Equal(250, resolved.SourceSegmentCount);
        Assert.Equal(130, resolved.TranslationSegmentCount);
    }

    private static void AddTranscriptWithSegments(
        GlosifyContext context,
        Guid transcriptId,
        int sourceSegments,
        int translationSegments)
    {
        context.RealtimeTranslationTranscripts.Add(new RealtimeTranslationTranscript
        {
            Id = transcriptId,
            UserId = "owner",
            Title = "Lesson recording",
            TargetLanguage = "pl",
            Stream = RealtimeTranslationTranscriptStreams.Source,
        });
        Add(RealtimeTranslationTranscriptStreams.Source, sourceSegments);
        Add(RealtimeTranslationTranscriptStreams.Translation, translationSegments);

        void Add(string stream, int count)
        {
            for (var index = 0; index < count; index++)
            {
                context.RealtimeTranslationTranscriptSegments.Add(new RealtimeTranslationTranscriptSegment
                {
                    Id = Guid.NewGuid(),
                    TranscriptId = transcriptId,
                    SessionId = Guid.NewGuid(),
                    Sequence = index,
                    Stream = stream,
                    ProviderEventKey = $"{stream}:{index}",
                    Text = $"{stream} {index}",
                });
            }
        }
    }

    private static GlosifyContext CreateContext() => new(
        new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options);

    private sealed class FixedLanguageContext(string language) : ILanguageContext
    {
        public string? CurrentLanguage => language;
        public IReadOnlyList<string> SupportedLanguages => [language];
        public bool TrySetLanguage(string value) => false;
        public void Clear() { }
    }

    private sealed class FixedLanguagePreference : IQuizLanguagePreferenceService
    {
        public Task<QuizLanguage?> GetSelectedAsync(string userId, CancellationToken cancellationToken = default) =>
            Task.FromResult(QuizLanguageCatalog.Find("pl"));

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
    }
}
