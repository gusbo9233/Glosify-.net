using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiControllerLanguageTests
{
    private const string UserId = "anki-controller-user";
    private static readonly DateTimeOffset Now = new(2026, 8, 17, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("invalid")]
    [InlineData("")]
    public async Task Invalid_direction_returns_validation_feedback_without_changing_the_collection(string direction)
    {
        await using var context = CreateContext();
        await CreateCollectionService(context).GetOrCreateAsync(UserId, "Polish", "UTC");
        var original = (await context.AnkiCollections.SingleAsync()).DefaultDirection;
        var controller = CreateController(context, "Polish");
        var result = Assert.IsType<RedirectToActionResult>(await controller.Direction(direction, default));
        Assert.Equal(nameof(AnkiController.Index), result.ActionName);
        Assert.NotEmpty(Assert.IsType<string>(controller.TempData["AnkiMessage"]));
        Assert.Equal(original, (await context.AnkiCollections.SingleAsync()).DefaultDirection);
    }

    [Fact]
    public async Task Direction_is_saved_on_the_current_language_collection()
    {
        await using var context = CreateContext();
        var collections = CreateCollectionService(context);
        var spanish = await collections.GetOrCreateAsync(UserId, "Spanish", "UTC");
        var controller = CreateController(context, "Polish");

        await controller.Direction(PracticeDirection.TargetToSource, default);

        var polish = await context.AnkiCollections.SingleAsync(c => c.TargetLanguage == "Polish");
        Assert.Equal(PracticeDirection.TargetToSource, polish.DefaultDirection);
        Assert.Equal(PracticeDirection.SourceToTarget, (await context.AnkiCollections.SingleAsync(c => c.Id == spanish.Id)).DefaultDirection);
    }

    [Fact]
    public async Task Index_opens_the_current_language_collection_with_the_next_card()
    {
        await using var context = CreateContext();
        var (quiz, word) = await SeedQuizAsync(context, "Polish");
        var collections = CreateCollectionService(context);
        var polish = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        await collections.GetOrCreateAsync(UserId, "Spanish", "UTC");
        Assert.True(await collections.AddItemAsync(new(polish.Id, quiz.Id, "words", word.Id, true, true), UserId));
        var controller = CreateController(context, "Polish");

        var result = await controller.Index(cancellationToken: CancellationToken.None);

        var model = Assert.IsType<AnkiIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Equal(polish.Id, model.Workspace.Collection!.Id);
        Assert.Equal("Polish", model.TargetLanguage);
        Assert.Equal(PracticeDirection.SourceToTarget, model.Direction);
        var card = model.Study!.State.Card!;
        Assert.Equal("house", card.Prompt);
        Assert.Equal("English", card.PromptLanguage);
        Assert.Equal(1, model.Study.Statistics!.Counts.Total);
        Assert.False(model.Study.AnswerRevealed);
    }

    [Fact]
    public async Task Index_without_a_collection_lists_quizzes_and_does_not_create_one()
    {
        await using var context = CreateContext();
        await SeedQuizAsync(context, "Polish");
        var controller = CreateController(context, "Polish");

        var result = await controller.Index(cancellationToken: CancellationToken.None);

        var model = Assert.IsType<AnkiIndexViewModel>(Assert.IsType<ViewResult>(result).Model);
        Assert.Null(model.Workspace.Collection);
        Assert.Null(model.Study);
        Assert.Single(model.Workspace.CompatibleQuizzes);
        Assert.Empty(await context.AnkiCollections.ToListAsync());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AddQuiz_links_both_directions_and_optional_sentences(bool includeSentences)
    {
        await using var context = CreateContext();
        var (quiz, _) = await SeedQuizAsync(context, "Polish");
        var controller = CreateController(context, "Polish");

        var result = await controller.AddQuiz(new AddAnkiQuizForm { QuizId = quiz.Id, IncludeSentences = includeSentences, TimeZoneId = "Europe/Stockholm" }, default);

        Assert.Equal(nameof(AnkiController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
        var collection = await context.AnkiCollections.SingleAsync();
        Assert.Equal("Polish", collection.TargetLanguage);
        Assert.Equal("Europe/Stockholm", collection.TimeZoneId);
        var link = await context.AnkiQuizLinks.SingleAsync();
        Assert.True(link.WordsSourceToTarget && link.WordsTargetToSource);
        Assert.Equal(includeSentences, link.SentencesSourceToTarget);
        Assert.Equal(includeSentences, link.SentencesTargetToSource);
    }

    [Fact]
    public async Task AddQuiz_rejects_a_quiz_from_another_app_language()
    {
        await using var context = CreateContext();
        var (spanishQuiz, _) = await SeedQuizAsync(context, "Spanish");
        var controller = CreateController(context, "Polish");

        var result = await controller.AddQuiz(new AddAnkiQuizForm { QuizId = spanishQuiz.Id }, default);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(await context.AnkiQuizLinks.ToListAsync());
    }

    [Fact]
    public async Task Items_applies_posted_changes_and_rejects_malformed_keys()
    {
        await using var context = CreateContext();
        var (quiz, word) = await SeedQuizAsync(context, "Polish");
        var controller = CreateController(context, "Polish");

        Assert.IsType<BadRequestResult>(await controller.Items(new SaveAnkiItemsForm { Add = ["words:not-a-guid:x"] }, default));
        Assert.IsType<BadRequestResult>(await controller.Items(new SaveAnkiItemsForm { Add = [$"quizzes:{quiz.Id}:x"] }, default));
        Assert.Empty(await context.AnkiCollections.ToListAsync());

        var added = await controller.Items(new SaveAnkiItemsForm { Add = [$"words:{quiz.Id}:{word.Id}"] }, default);
        Assert.Equal(nameof(AnkiController.Index), Assert.IsType<RedirectToActionResult>(added).ActionName);
        Assert.Equal(2, await context.AnkiCards.CountAsync(card => card.IsActive));

        await controller.Items(new SaveAnkiItemsForm { Remove = [$"words:{quiz.Id}:{word.Id}"] }, default);
        Assert.Equal(0, await context.AnkiCards.CountAsync(card => card.IsActive));
        Assert.Equal("Removed 1 item from Anki.", controller.TempData["AnkiMessage"]);
    }

    [Fact]
    public async Task AddItem_from_the_quiz_page_adds_both_directions_and_returns_locally()
    {
        await using var context = CreateContext();
        var (quiz, word) = await SeedQuizAsync(context, "Polish");
        var controller = CreateController(context, "Polish");
        controller.Url = new LocalUrlHelper();

        var result = await controller.AddItem(new AddAnkiItemForm { QuizId = quiz.Id, ItemType = "word", ItemId = word.Id }, "/Quiz/Details/1", default);

        Assert.Equal("/Quiz/Details/1", Assert.IsType<LocalRedirectResult>(result).Url);
        Assert.Equal(2, await context.AnkiCards.CountAsync(card => card.IsActive));
    }

    [Fact]
    public async Task Rate_rejects_first_and_repeated_requests_without_a_client_token()
    {
        await using var context = CreateContext();
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = "Basics",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
            ProcessingStatus = "Ready",
            CreatedAt = Now,
        };
        var word = new Word
        {
            Id = $"word-{Guid.NewGuid():N}",
            QuizId = quiz.Id,
            Lemma = "dom",
            Translation = "house",
            CreatedAt = Now,
        };
        context.AddRange(quiz, word);
        await context.SaveChangesAsync();
        var collections = CreateCollectionService(context);
        var collection = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
        Assert.True(await collections.AddItemAsync(
            new(collection.Id, quiz.Id, "word", word.Id, true, false), UserId));
        var cardId = await context.AnkiCards.Select(card => card.Id).SingleAsync();

        var first = await CreateController(context, "Polish").Rate(new RateAnkiCardForm
        {
            CollectionId = collection.Id,
            CardId = cardId,
            Rating = AnkiRatings.Good,
            RowVersion = string.Empty,
        }, CancellationToken.None);
        var repeated = await CreateController(context, "Polish").Rate(new RateAnkiCardForm
        {
            CollectionId = collection.Id,
            CardId = cardId,
            Rating = AnkiRatings.Good,
            RowVersion = string.Empty,
        }, CancellationToken.None);

        Assert.Equal(nameof(AnkiController.Study), Assert.IsType<RedirectToActionResult>(first).ActionName);
        Assert.Equal(nameof(AnkiController.Study), Assert.IsType<RedirectToActionResult>(repeated).ActionName);
        Assert.Empty(await context.AnkiReviews.ToListAsync());
        Assert.Equal(0, (await context.AnkiCards.SingleAsync(card => card.Id == cardId)).ReviewCount);
    }

    [Fact]
    public async Task Rate_reports_an_invalid_rating_when_the_client_token_is_valid()
    {
        await using var context = CreateContext();
        var controller = CreateController(context, "Polish");
        controller.ModelState.AddModelError(
            nameof(RateAnkiCardForm.Rating),
            "Choose a supported rating.");

        var result = await controller.Rate(new RateAnkiCardForm
        {
            CollectionId = Guid.NewGuid(),
            CardId = Guid.NewGuid(),
            Rating = "invalid",
            ClientToken = Guid.NewGuid(),
        }, CancellationToken.None);

        Assert.Equal(nameof(AnkiController.Study),
            Assert.IsType<RedirectToActionResult>(result).ActionName);
        Assert.Equal(
            "Choose Again, Hard, Good, or Easy to rate the card.",
            controller.TempData["AnkiMessage"]);
    }

    [Fact]
    public void Old_collection_links_redirect_to_the_single_page()
    {
        using var context = CreateContext();
        var result = CreateController(context, "Polish").Collection(Guid.NewGuid());

        var redirect = Assert.IsType<RedirectToActionResult>(result);
        Assert.Equal(nameof(AnkiController.Index), redirect.ActionName);
        Assert.True(redirect.Permanent);
    }

    [Fact]
    public async Task Study_partial_cannot_open_a_collection_from_another_app_language()
    {
        await using var context = CreateContext();
        await CreateCollectionService(context).GetOrCreateAsync(UserId, "Spanish", "UTC");
        var controller = CreateController(context, "Polish");
        controller.Request.Headers.XRequestedWith = "XMLHttpRequest";

        var result = await controller.Study(cancellationToken: CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Study_full_page_requests_redirect_to_the_single_page()
    {
        await using var context = CreateContext();
        var controller = CreateController(context, "Polish");

        var result = await controller.Study(Guid.NewGuid(), cancellationToken: CancellationToken.None);

        Assert.Equal(nameof(AnkiController.Index), Assert.IsType<RedirectToActionResult>(result).ActionName);
    }

    [Fact]
    public async Task Rate_cannot_review_a_card_from_another_app_language()
    {
        await using var context = CreateContext();
        var (quiz, word) = await SeedQuizAsync(context, "Spanish");
        var collections = CreateCollectionService(context);
        var spanish = await collections.GetOrCreateAsync(UserId, "Spanish", "UTC");
        Assert.True(await collections.AddItemAsync(new(spanish.Id, quiz.Id, "words", word.Id, true, false), UserId));
        var cardId = await context.AnkiCards.Select(card => card.Id).SingleAsync();

        var result = await CreateController(context, "Polish").Rate(new RateAnkiCardForm
        {
            CollectionId = spanish.Id,
            CardId = cardId,
            Rating = AnkiRatings.Good,
            ClientToken = Guid.NewGuid(),
        }, CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
        Assert.Empty(await context.AnkiReviews.ToListAsync());
    }

    private static async Task<(Quiz Quiz, Word Word)> SeedQuizAsync(GlosifyContext context, string targetLanguage)
    {
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            UserId = UserId,
            Name = $"{targetLanguage} basics",
            SourceLanguage = "English",
            TargetLanguage = targetLanguage,
            Language = targetLanguage,
            ProcessingStatus = "Ready",
            CreatedAt = Now,
        };
        var word = new Word { Id = $"word-{Guid.NewGuid():N}", QuizId = quiz.Id, Lemma = "dom", Translation = "house", CreatedAt = Now };
        context.AddRange(quiz, word,
            new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "To jest dom.", Translation = "This is a house.", CreatedAt = Now });
        await context.SaveChangesAsync();
        return (quiz, word);
    }

    private static AnkiController CreateController(GlosifyContext context, string language)
    {
        var collections = CreateCollectionService(context);
        var clock = new FakeTimeProvider(Now);
        var identity = new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "Test");
        var httpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) };
        return new AnkiController(
            collections,
            new AnkiStudyService(context, collections, new Fsrs6AnkiScheduler(), clock),
            new AnkiStatisticsService(context, collections, clock),
            new FixedLanguageContext(language))
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = httpContext,
            },
            TempData = new TempDataDictionary(httpContext, new InMemoryTempDataProvider()),
        };
    }

    private static AnkiCollectionService CreateCollectionService(GlosifyContext context) =>
        new(context, new FakeTimeProvider(Now));

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        var context = new GlosifyContext(options);
        context.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki-controller@example.test" });
        context.SaveChanges();
        return context;
    }

    private sealed class FixedLanguageContext(string currentLanguage) : ILanguageContext
    {
        public string? CurrentLanguage => currentLanguage;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["English", "Polish", "Spanish", "Freestyle"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }

    private sealed class LocalUrlHelper : IUrlHelper
    {
        public ActionContext ActionContext { get; } = new();
        public string? Action(UrlActionContext actionContext) => null;
        public string? Content(string? contentPath) => contentPath;
        public bool IsLocalUrl(string? url) => url?.StartsWith('/') == true && !url.StartsWith("//", StringComparison.Ordinal);
        public string? Link(string? routeName, object? values) => null;
        public string? RouteUrl(UrlRouteContext routeContext) => null;
    }

    private sealed class InMemoryTempDataProvider : ITempDataProvider
    {
        private Dictionary<string, object> _values = [];

        public IDictionary<string, object> LoadTempData(HttpContext context) => _values;

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) =>
            _values = new Dictionary<string, object>(values, StringComparer.Ordinal);
    }
}
