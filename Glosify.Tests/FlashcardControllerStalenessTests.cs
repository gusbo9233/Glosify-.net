using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class FlashcardControllerStalenessTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Sentence_cards_reveal_with_loader_ids_and_reject_changed_content(bool changed)
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = "user", Name = "Sentences", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        var sentence = new QuizSentence { Id = Guid.NewGuid(), QuizId = quiz.Id, Text = "To jest dom.", Translation = "This is a house." };
        db.Quizzes.Add(quiz);
        db.QuizSentences.Add(sentence);
        await db.SaveChangesAsync();
        var words = new WordService(db, null!);
        var loaded = await words.LoadSentenceCardsAsync(quiz.Id, 10);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var sessions = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var session = sessions.StartSession("user", quiz.Id, quiz.Name, "English", "Polish", 10,
            loaded.Select(c => new FlashcardCardData { Id = c.Id, Lemma = c.Lemma, Translation = c.Translation }).ToList(),
            practiceItemType: "sentences");
        sessions.SaveSession(session);
        if (changed) { sentence.Text = "Edited after starting"; await db.SaveChangesAsync(); }
        var controller = new FlashcardQuizController(new QuizService(db, null!, null!), words, sessions, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
            } }
        };
        var result = await controller.Reveal(session.SessionId, expectedIndex: 0);
        if (changed)
        {
            Assert.Equal(409, Assert.IsType<ObjectResult>(result).StatusCode);
            Assert.False(session.IsAnswerRevealed);
            Assert.Null(sessions.FindSession(session.SessionId, "user"));
        }
        else
        {
            Assert.IsType<ViewResult>(result);
            Assert.True(session.IsAnswerRevealed);
        }
    }
}
