using System.Collections;
using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Models.ViewModels;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Words;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class FlashcardResponseConcurrencyTests
{
    [Fact]
    public async Task Reveal_response_remains_consistent_when_another_request_rates_the_card()
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = "user", Name = "Cards",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.Quizzes.Add(quiz);
        db.Words.Add(new Word { Id = "word", QuizId = quiz.Id, Lemma = "Dom", Translation = "House" });
        await db.SaveChangesAsync();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var service = new FlashcardSessionService(cache, new QuizSessionRegistry(cache));
        var card = new FlashcardCardData { Id = "word", Lemma = "Dom", Translation = "House" };
        using var renderReached = new ManualResetEventSlim();
        using var ratingAttempting = new ManualResetEventSlim();
        using var ratingFinished = new ManualResetEventSlim();
        Thread? rating = null;
        Exception? ratingFailure = null;
        var reachedRating = false;
        // Ownership checks enumerate the cards; response construction indexes them.
        // Pause that read until the rating finishes or waits on the session lock.
        var cards = new ObservedCards(card, () =>
        {
            renderReached.Set();
            reachedRating = ratingAttempting.Wait(TimeSpan.FromSeconds(5)) && SpinWait.SpinUntil(() => ratingFinished.IsSet
                || (rating!.ThreadState & ThreadState.WaitSleepJoin) != 0, TimeSpan.FromSeconds(5));
        });
        var session = service.StartSession("user", quiz.Id, quiz.Name, "English", "Polish", 1, [card])
            with { Cards = cards };
        service.SaveSession(session);
        rating = new Thread(() =>
        {
            try
            {
                if (!renderReached.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
                ratingAttempting.Set();
                lock (session) service.ApplyRating(session, "good");
            }
            catch (Exception exception) { ratingFailure = exception; }
            finally { ratingFinished.Set(); }
        });
        var controller = new FlashcardQuizController(new QuizService(db, null!, null!),
            new WordService(db, null!), service, null!)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test"))
            } }
        };
        rating.Start();
        var response = await controller.Reveal(session.SessionId, expectedIndex: 0);
        Assert.True(rating.Join(TimeSpan.FromSeconds(5)));
        Assert.True(reachedRating);
        Assert.Null(ratingFailure);
        Assert.Equal(1, session.CurrentIndex);
        var model = Assert.IsType<FlashcardQuizViewModel>(Assert.IsType<ViewResult>(response).Model);
        Assert.NotNull(model.CurrentCard);
        Assert.Equal(0, model.CurrentIndex);
        Assert.Equal(0, model.CompletedCards);
        Assert.Equal(0, model.RememberedCount);
        Assert.True(model.IsAnswerRevealed);
        Assert.False(model.IsComplete);
    }

    private sealed class ObservedCards(FlashcardCardData card, Action onResponseRead) : IReadOnlyList<FlashcardCardData>
    {
        private int reads;
        public int Count => 1;
        public FlashcardCardData this[int index]
        {
            get
            {
                if (index != 0) throw new ArgumentOutOfRangeException(nameof(index));
                if (Interlocked.Increment(ref reads) == 1) onResponseRead();
                return card;
            }
        }
        public IEnumerator<FlashcardCardData> GetEnumerator() => ((IEnumerable<FlashcardCardData>)new[] { card }).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
