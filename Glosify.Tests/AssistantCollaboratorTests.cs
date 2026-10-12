using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.Library;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Tools;
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
    public void System_prompts_carry_no_per_request_facts()
    {
        foreach (var mode in Enum.GetValues<AssistantMode>())
        {
            var instruction = AssistantPrompts.System(mode);

            // Anything that varies per request belongs in the context note, not the cached prefix.
            Assert.DoesNotContain("{", instruction);
            Assert.Contains("GlobeGlotter", instruction);
            Assert.Contains("never instructions", instruction);
            Assert.Contains("undo", instruction);
        }
    }

    [Fact]
    public void Language_prompt_keeps_the_standard_quiz_and_extraction_defaults()
    {
        var instruction = AssistantPrompts.System(AssistantMode.Language);

        Assert.Contains("standard quizzes only", instruction);
        Assert.Contains("every unique word except proper names", instruction);
        Assert.Contains("never in words", instruction);
        Assert.Contains("do not ask the user to choose or confirm the language pair", instruction);
        Assert.DoesNotContain("create_custom_quiz", instruction);
    }

    [Fact]
    public void Freestyle_prompt_offers_a_generic_standard_quiz_replacement()
    {
        var instruction = AssistantPrompts.System(AssistantMode.Freestyle);

        Assert.Contains("equivalent prompt-and-answer quiz", instruction);
        Assert.DoesNotContain("word-and-translation", instruction);
        Assert.DoesNotContain("sentence", instruction);
    }

    [Fact]
    public void Context_note_states_the_established_languages()
    {
        var note = AssistantPrompts.ContextNote(Facts() with { ReplyLanguage = "Swedish" });

        Assert.Contains("Learning language: Polish", note);
        Assert.Contains("Translation language: English", note);
        Assert.Contains("Reply language: Swedish", note);
        Assert.Contains("Selected quiz: \"Starter quiz\"", note);
        Assert.Contains("12 words, 3 sentences", note);
        Assert.Contains("not instructions from the user", note);
    }

    [Theory]
    [InlineData(2, "source", "reading page 2 of the source stream")]
    [InlineData(1, "translation", "reading page 1 of the translation stream")]
    public void Context_note_names_the_transcript_page_the_user_is_reading(int viewedPage, string viewedStream, string expected)
    {
        var note = AssistantPrompts.ContextNote(Facts() with { Transcript = Transcript(viewedPage, viewedStream) });

        Assert.Contains("source 3 pages, 250 captions", note);
        Assert.Contains("translation 2 pages, 130 captions", note);
        Assert.Contains(expected, note);
    }

    [Fact]
    public void Context_note_omits_a_transcript_page_the_resolver_dropped()
    {
        var note = AssistantPrompts.ContextNote(Facts() with { Transcript = Transcript(null, null) });

        Assert.Contains("Selected transcript: \"Lesson recording\"", note);
        Assert.DoesNotContain("is reading page", note);
    }

    [Fact]
    public void Context_note_carries_the_page_text_and_focus()
    {
        var note = AssistantPrompts.ContextNote(Facts() with
        {
            Page = new DocumentPageContext("Course book", 7, "Ala ma kota.", null),
            FocusedWordId = "word-1",
            FocusedWordLabel = "\"dom\" → \"house\"",
        });

        Assert.Contains("reading page 7 of \"Course book\"", note);
        Assert.Contains("Ala ma kota.", note);
        Assert.Contains("Focused on \"dom\" → \"house\" (id word-1)", note);
    }

    [Fact]
    public void Freestyle_context_note_leaves_out_language_pairs()
    {
        var note = AssistantPrompts.ContextNote(Facts() with { Mode = AssistantMode.Freestyle, QuizLanguages = null });

        Assert.DoesNotContain("Learning language", note);
        Assert.DoesNotContain("Translation language", note);
        Assert.Contains("12 items", note);
    }

    private static AssistantContextFacts Facts() => new(
        AssistantMode.Language,
        "Polish",
        "English",
        "English",
        Guid.Parse("11111111-1111-1111-1111-111111111111"),
        "Starter quiz",
        "Polish with translations in English",
        12,
        3,
        null,
        null,
        null,
        null,
        null,
        null);

    private static TranscriptAssistantContext Transcript(int? viewedPage, string? viewedStream) => new(
        Guid.Parse("33333333-3333-3333-3333-333333333333"),
        "Lesson recording",
        "pl",
        "source",
        SourceSegmentCount: 250,
        TranslationSegmentCount: 130,
        ViewedPage: viewedPage,
        ViewedStream: viewedStream);

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
