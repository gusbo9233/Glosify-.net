using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Services.Flashcards;
using Glosify.Services.Language;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizContentSelectionTests
{
    [Theory]
    [InlineData("typing", "Spanish", "sentences", true)]
    [InlineData("flashcards", "Spanish", "sentences", true)]
    [InlineData("typing", "Spanish", "words", false)]
    [InlineData("flashcards", "Spanish", "words", false)]
    [InlineData("typing", "Freestyle", "sentences", false)]
    [InlineData("flashcards", "Freestyle", "sentences", false)]
    public async Task DirectPracticeHonorsEffectiveContentType(string mode, string language, string requestedType, bool sentences)
    {
        await using var db = CreateContext();
        var quizId = await SeedAsync(db, language);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var registry = new QuizSessionRegistry(cache);
        var quizzes = new QuizService(db, new CookieLanguageContext(new HttpContextAccessor()), null!);
        var words = new WordService(db, null!);
        var typing = new TypingQuizService(db);
        string? selection;
        string itemType;
        string answer;
        int count;
        if (mode == "typing")
        {
            var sessions = new TypingSessionService(cache, typing, registry);
            var controller = Authenticate(new TypingQuizController(quizzes, typing, sessions, null!));
            var redirect = Assert.IsType<RedirectToActionResult>(await controller.Index(quizId, 2,
                practiceItemType: requestedType, selectedWordIds: "word-1"));
            var session = sessions.FindSession(Assert.IsType<string>(redirect.RouteValues!["sessionId"]), "user");
            Assert.NotNull(session);
            selection = session.SelectedWordIds;
            itemType = session.PracticeItemType;
            answer = session.Words[0].Answer;
            count = session.Words.Count;
        }
        else
        {
            var sessions = new FlashcardSessionService(cache, registry);
            var controller = Authenticate(new FlashcardQuizController(quizzes, words, sessions, null!));
            var view = Assert.IsType<ViewResult>(await controller.Index(quizId, 2,
                practiceItemType: requestedType, selectedWordIds: "word-1"));
            var model = Assert.IsType<FlashcardQuizViewModel>(view.Model);
            selection = model.SelectedWordIds;
            itemType = model.PracticeItemType;
            Assert.NotNull(model.CurrentCard);
            answer = model.CurrentCard.Answer;
            count = model.TotalCards;
        }
        Assert.Equal(sentences ? PracticeItemType.Sentences : PracticeItemType.Words, itemType);
        Assert.Equal(sentences ? null : "word-1", selection);
        Assert.Equal(sentences ? 2 : 1, count);
        if (sentences) Assert.StartsWith("Sentence ", answer);
        else Assert.Equal(language == "Freestyle" ? "house" : "casa", answer);
    }

    [Theory]
    [InlineData("typing", "Spanish", null)]
    [InlineData("flashcards", "Spanish", null)]
    [InlineData("typing", "Freestyle", "word-1")]
    [InlineData("flashcards", "Freestyle", "word-1")]
    public async Task SettingsRedirectCarriesOnlyCompatibleSelection(string mode, string language, string? expected)
    {
        await using var db = CreateContext();
        var quizId = await SeedAsync(db, language);
        var languageContext = new CookieLanguageContext(new HttpContextAccessor());
        var controller = Authenticate(new QuizController(new QuizService(db, languageContext, null!),
            null!, new WordService(db, null!), null!, languageContext, null!));
        var result = Assert.IsType<RedirectToActionResult>(await controller.Start(new QuizSessionSettings
        {
            QuizId = quizId, Mode = mode, WordCount = 2,
            PracticeItemType = PracticeItemType.Sentences, SelectedWordIds = "word-1"
        }));
        Assert.Equal(expected, result.RouteValues!["selectedWordIds"]);
        Assert.Equal(language == "Freestyle" ? "words" : "sentences", result.RouteValues["practiceItemType"]);
        Assert.Equal(mode == "typing" ? "TypingQuiz" : "FlashcardQuiz", result.ControllerName);
    }

    [Fact]
    public async Task TypingLoaderUsesSentencesEvenWhenCalledWithWordIds()
    {
        await using var db = CreateContext();
        var quizId = await SeedAsync(db, "Spanish");
        var result = await new TypingQuizService(db).GetQuizDataAsync(quizId, 2,
            practiceItemType: "sentences", wordIds: ["word-1"]);
        Assert.Equal(2, result.Words.Count);
        Assert.All(result.Words, word => Assert.StartsWith("Sentence ", word.Answer));
    }

    private static T Authenticate<T>(T controller) where T : Controller
    {
        controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
        } };
        return controller;
    }

    private static GlosifyContext CreateContext() => new(new DbContextOptionsBuilder<GlosifyContext>()
        .UseInMemoryDatabase(Guid.NewGuid().ToString("N")).Options);

    private static async Task<Guid> SeedAsync(GlosifyContext db, string language)
    {
        var id = Guid.NewGuid();
        db.Quizzes.Add(new Quiz { Id = id, UserId = "user", Name = "Quiz", SourceLanguage = "English",
            TargetLanguage = language, Language = language, ProcessingStatus = "Ready" });
        db.Words.Add(new Word { Id = "word-1", QuizId = id, Lemma = "casa", Translation = "house" });
        for (var i = 0; i < 2; i++) db.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(), QuizId = id, Text = $"Sentence {i}", Translation = $"Translation {i}"
        });
        await db.SaveChangesAsync();
        return id;
    }
}
