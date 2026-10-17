using System.Net;
using System.Security.Claims;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Abuse;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiPageTests : IDisposable
{
    private const string UserId = "anki-page-user";
    private readonly WebApplicationFactory<Program> _factory;
    private readonly HttpClient _client;
    private readonly ItemsFormCapture _formCapture = new();

    public AnkiPageTests()
    {
        var database = Guid.NewGuid().ToString();
        _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureTestServices(services =>
            {
                services.Configure<MvcOptions>(options => options.Filters.Add(_formCapture));
                // The test seeds accounting readiness; avoid racing the background backfill.
                foreach (var registration in services.Where(service => service.ServiceType == typeof(IHostedService)
                    && service.ImplementationType == typeof(ResourceMaintenanceService)).ToArray())
                    services.Remove(registration);
                services.RemoveAll<DbContextOptions<GlosifyContext>>();
                services.RemoveAll<IDbContextOptionsConfiguration<GlosifyContext>>();
                services.AddDbContext<GlosifyContext>(options => options.UseInMemoryDatabase(database));
                services.RemoveAll<ILanguageContext>();
                services.AddSingleton<ILanguageContext, FixedLanguageContext>();
                services.RemoveAll<IPolicyEvaluator>();
                services.AddSingleton<IPolicyEvaluator, AuthenticatedPolicyEvaluator>();
            });
        });
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Theory]
    [InlineData(1023, 0)]
    [InlineData(2000, 0)]
    [InlineData(0, 2000)]
    [InlineData(1000, 1000)]
    public async Task Items_binds_supported_batches_with_antiforgery_and_timezone(int addCount, int removeCount)
    {
        var quizId = await SeedAsync();
        _formCapture.CaptureOnly = true;
        var keys = Enumerable.Range(0, addCount + removeCount).Select(i => $"words:{quizId}:word-{i}").ToList();
        var page = await ReadPageAsync();
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", Token(page)),
            new("TimeZoneId", "Europe/Stockholm"),
        };
        fields.AddRange(keys.Select((key, i) => new KeyValuePair<string, string>(i < addCount ? "Add" : "Remove", key)));

        var response = await _client.PostAsync("/Anki/Items", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var bound = Assert.IsType<SaveAnkiItemsForm>(_formCapture.Form);
        Assert.Equal(keys.Take(addCount), bound.Add);
        Assert.Equal(keys.Skip(addCount), bound.Remove);
        Assert.Equal("Europe/Stockholm", bound.TimeZoneId);
        Assert.True(_formCapture.ModelStateValid);
    }

    [Fact]
    public async Task Items_persists_the_bound_changes()
    {
        var quizId = await SeedAsync();
        var page = await ReadPageAsync();

        var response = await _client.PostAsync("/Anki/Items", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(page),
            ["TimeZoneId"] = "Europe/Stockholm",
            ["Add"] = $"words:{quizId}:word-0",
        }));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        Assert.Equal(2, await db.AnkiCards.CountAsync(card => card.IsActive && card.Note.WordId == "word-0"));
        Assert.Equal("Europe/Stockholm", (await db.AnkiCollections.SingleAsync()).TimeZoneId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Items_rejects_missing_antiforgery_and_oversized_forms(bool oversized)
    {
        var quizId = await SeedAsync();
        _formCapture.CaptureOnly = true;
        var page = await ReadPageAsync();
        var fields = new List<KeyValuePair<string, string>> { new("TimeZoneId", "UTC") };
        if (oversized) fields.Add(new("__RequestVerificationToken", Token(page)));
        fields.AddRange(Enumerable.Range(0, oversized ? 2001 : 1)
            .Select(i => new KeyValuePair<string, string>("Add", $"words:{quizId}:word-{i}")));

        var response = await _client.PostAsync("/Anki/Items", new FormUrlEncodedContent(fields));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(_formCapture.Form);
        await using var scope = _factory.Services.CreateAsyncScope();
        Assert.Empty(await scope.ServiceProvider.GetRequiredService<GlosifyContext>().AnkiCards.ToListAsync());
    }

    [Theory]
    [InlineData(PracticeDirection.SourceToTarget)]
    [InlineData(PracticeDirection.TargetToSource)]
    public async Task Empty_direction_offers_a_switch_to_existing_cards(string selectedDirection)
    {
        var quizId = await SeedAsync();
        var otherDirection = selectedDirection == PracticeDirection.SourceToTarget
            ? PracticeDirection.TargetToSource : PracticeDirection.SourceToTarget;
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var collections = scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>();
            var collection = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
            await collections.SetPracticeDirectionAsync(collection.Id, selectedDirection, UserId);
            await collections.AddItemAsync(new(collection.Id, quizId, "words", "word-0",
                otherDirection == PracticeDirection.SourceToTarget, otherDirection == PracticeDirection.TargetToSource), UserId);
        }

        var page = await ReadPageAsync();

        Assert.Equal("No cards in this direction", page.QuerySelector(".anki-empty h2")?.TextContent);
        Assert.NotNull(page.QuerySelector("[data-anki-item-key][checked]"));
        var switchButton = Assert.Single(page.QuerySelectorAll(".anki-empty button[name='direction']"));
        Assert.Equal(otherDirection, switchButton.GetAttribute("value"));
        var form = switchButton.Closest("form")!;
        var response = await _client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["direction"] = otherDirection,
            ["__RequestVerificationToken"] = Token(form),
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var switched = await ReadPageAsync();
        Assert.NotNull(switched.QuerySelector("[data-card-reveal-form]"));
        Assert.Null(switched.QuerySelector(".anki-empty"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Empty_collection_still_offers_settings(bool previouslyHadCards)
    {
        var quizId = await SeedAsync();
        if (previouslyHadCards)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var collections = scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>();
            var collection = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
            await collections.AddItemAsync(new(collection.Id, quizId, "words", "word-0", true, true), UserId);
            await collections.SetMembershipAsync(collection.Id, [], [new(quizId, "words", "word-0")], UserId);
        }

        var page = await ReadPageAsync();

        Assert.Equal("Your Anki collection is empty", page.QuerySelector(".anki-empty h2")?.TextContent);
        Assert.NotNull(page.QuerySelector("[data-anki-open-settings]"));
        Assert.Null(page.QuerySelector(".anki-empty button[name='direction']"));
        Assert.All(page.QuerySelectorAll(".anki-direction button"), button =>
            Assert.Equal(!previouslyHadCards, button.HasAttribute("disabled")));
        Assert.Null(page.QuerySelector("form[action='/Anki/Settings']"));
    }

    private async Task<Guid> SeedAsync()
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await db.SaveChangesAsync();
        db.Users.Add(new ApplicationUser { Id = UserId, UserName = "anki@example.test" });
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(new Quiz
        {
            Id = quizId, UserId = UserId, Name = "Basics", SourceLanguage = "English",
            TargetLanguage = "Polish", Language = "Polish", ProcessingStatus = "Ready",
        });
        db.Words.Add(new Word { Id = "word-0", QuizId = quizId, Lemma = "dom", Translation = "house" });
        await db.SaveChangesAsync();
        return quizId;
    }

    [Theory]
    [InlineData(true, PracticeDirection.SourceToTarget)]
    [InlineData(true, PracticeDirection.TargetToSource)]
    [InlineData(false, PracticeDirection.SourceToTarget)]
    [InlineData(false, PracticeDirection.TargetToSource)]
    public async Task Study_cards_only_pronounce_target_text_before_and_after_reveal(bool anki, string direction)
    {
        var quizId = await SeedAsync();
        if (anki)
        {
            await using var scope = _factory.Services.CreateAsyncScope();
            var collections = scope.ServiceProvider.GetRequiredService<IAnkiCollectionService>();
            var collection = await collections.GetOrCreateAsync(UserId, "Polish", "UTC");
            await collections.AddItemAsync(new(collection.Id, quizId, "words", "word-0", true, true), UserId);
            await collections.SetPracticeDirectionAsync(collection.Id, direction, UserId);
        }
        var url = anki ? "/Anki" : $"/FlashcardQuiz?id={quizId}&practiceDirection={direction}";
        var response = await _client.GetAsync(url);
        response.EnsureSuccessStatusCode();
        var page = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        var promptIsTarget = direction == PracticeDirection.TargetToSource;
        var card = page.QuerySelector(".flashcard")!;
        Assert.Equal(promptIsTarget ? "dom" : "house", card.QuerySelector(".card-text")?.TextContent);
        Assert.Null(card.QuerySelector(".card-answer"));
        AssertTargetPronunciation(card, promptIsTarget);

        var form = page.QuerySelector("[data-card-reveal-form]")!;
        Assert.NotNull(form.QuerySelector("button[type='submit'][aria-label]"));
        Assert.Empty(form.QuerySelectorAll("button button"));
        var fields = form.QuerySelectorAll("input[name]").Select(input =>
            new KeyValuePair<string, string>(input.GetAttribute("name")!, input.GetAttribute("value") ?? ""));
        _client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        response = await _client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(fields));
        if (response.StatusCode == HttpStatusCode.Redirect)
            response = await _client.GetAsync(response.Headers.Location);
        response.EnsureSuccessStatusCode();
        var revealed = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        card = revealed.QuerySelector(".flashcard.is-revealed")!;
        Assert.NotNull(card);
        Assert.Equal(promptIsTarget ? "house" : "dom", card.QuerySelector(".translation .card-text")?.TextContent);
        AssertTargetPronunciation(card, true);
        Assert.Equal(anki ? 4 : 2, revealed.QuerySelectorAll("button[name='rating']").Length);
        Assert.Equal(anki ? new[] { "again", "hard", "good", "easy" } : ["again", "good"],
            revealed.QuerySelectorAll("button[name='rating']").Select(button => button.GetAttribute("value")));
        Assert.NotEmpty(Token(revealed));
    }

    [Fact]
    public async Task Freestyle_flashcards_have_no_pronunciation_after_reveal()
    {
        var quizId = await SeedAsync();
        await using (var scope = _factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<GlosifyContext>();
            var quiz = await db.Quizzes.SingleAsync();
            quiz.TargetLanguage = "Freestyle";
            quiz.SourceLanguage = "Freestyle";
            quiz.Language = "Freestyle";
            await db.SaveChangesAsync();
        }
        ((FixedLanguageContext)_factory.Services.GetRequiredService<ILanguageContext>()).CurrentLanguage = "Freestyle";
        var response = await _client.GetAsync($"/FlashcardQuiz?id={quizId}");
        response.EnsureSuccessStatusCode();
        var page = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        Assert.Empty(page.QuerySelectorAll(".flashcard [data-tts]"));
        var form = page.QuerySelector("[data-card-reveal-form]")!;
        _client.DefaultRequestHeaders.Add("X-Requested-With", "XMLHttpRequest");
        response = await _client.PostAsync(form.GetAttribute("action"), new FormUrlEncodedContent(
            form.QuerySelectorAll("input[name]").Select(input => new KeyValuePair<string, string>(
                input.GetAttribute("name")!, input.GetAttribute("value") ?? ""))));
        response.EnsureSuccessStatusCode();
        var revealed = await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
        Assert.NotNull(revealed.QuerySelector(".flashcard.is-revealed"));
        Assert.Empty(revealed.QuerySelectorAll(".flashcard [data-tts]"));
    }

    private static void AssertTargetPronunciation(IElement card, bool visible)
    {
        var buttons = card.QuerySelectorAll("[data-tts]");
        if (!visible)
        {
            Assert.Empty(buttons);
            return;
        }
        var button = Assert.Single(buttons);
        Assert.Equal("dom", button.GetAttribute("data-tts"));
        Assert.Equal("Polish", button.GetAttribute("data-tts-lang"));
        Assert.Equal("button", button.GetAttribute("type"));
        Assert.Equal("false", button.GetAttribute("aria-pressed"));
        Assert.Equal("dom", button.PreviousElementSibling?.TextContent);
        Assert.True(button.ParentElement!.ClassList.Contains("card-text-row"));
        Assert.Equal(button, button.ParentElement.LastElementChild);
    }

    private async Task<IDocument> ReadPageAsync()
    {
        var response = await _client.GetAsync("/Anki");
        response.EnsureSuccessStatusCode();
        return await new HtmlParser().ParseDocumentAsync(await response.Content.ReadAsStringAsync());
    }

    private static string Token(IParentNode node) =>
        node.QuerySelector("input[name='__RequestVerificationToken']")!.GetAttribute("value")!;

    public void Dispose()
    {
        _client.Dispose();
        _factory.Dispose();
    }

    // Capture after the real authorization, antiforgery and model-binding pipeline.
    // Bulk persistence is covered by service tests; these cases target HTTP limits.
    private sealed class ItemsFormCapture : IActionFilter
    {
        public bool CaptureOnly { get; set; }
        public SaveAnkiItemsForm? Form { get; private set; }
        public bool ModelStateValid { get; private set; }

        public void OnActionExecuting(ActionExecutingContext context)
        {
            if (!CaptureOnly || context.Controller is not AnkiController
                || context.ActionDescriptor.RouteValues["action"] != nameof(AnkiController.Items)) return;
            Form = (SaveAnkiItemsForm)context.ActionArguments["form"]!;
            ModelStateValid = context.ModelState.IsValid;
            context.Result = new NoContentResult();
        }

        public void OnActionExecuted(ActionExecutedContext context) { }
    }

    private sealed class FixedLanguageContext : ILanguageContext
    {
        public string CurrentLanguage { get; set; } = "Polish";
        public IReadOnlyList<string> SupportedLanguages => ["Polish"];
        public bool TrySetLanguage(string language) => false;
        public void Clear() { }
    }

    private sealed class AuthenticatedPolicyEvaluator : IPolicyEvaluator
    {
        public Task<AuthenticateResult> AuthenticateAsync(AuthorizationPolicy policy, HttpContext context)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, UserId)], "test"));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(context.User, "test")));
        }

        public Task<PolicyAuthorizationResult> AuthorizeAsync(AuthorizationPolicy policy,
            AuthenticateResult authenticationResult, HttpContext context, object? resource) =>
            Task.FromResult(PolicyAuthorizationResult.Success());
    }
}
