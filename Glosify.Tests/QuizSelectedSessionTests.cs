using System.Security.Claims;
using Glosify.Controllers;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizSelectedSessionTests
{
    [Theory]
    [InlineData("typing", 0, 100)]
    [InlineData("flashcards", 0, 100)]
    [InlineData("typing", 20, 80)]
    [InlineData("flashcards", 20, 80)]
    public void SelectedSessionsDoNotShadowGenericResumeOrReset(string mode, int start, int end)
    {
        using var fixture = new Fixture(mode, start, end);
        var selected = fixture.Start("one,two");
        fixture.Save(selected);
        Assert.Null(fixture.Resume());
        Assert.Same(selected, fixture.Find(selected.SessionId));
        var generic = fixture.Start(null);
        fixture.Save(generic);
        Assert.Same(generic, fixture.Resume());
        fixture.Reset();
        Assert.Null(fixture.Resume());
        Assert.Null(fixture.Find(generic.SessionId));
        Assert.Same(selected, fixture.Find(selected.SessionId));
    }

    [Theory]
    [InlineData("typing")]
    [InlineData("flashcards")]
    public void SelectedSessionsStillParticipateInPerUserEviction(string mode)
    {
        using var fixture = new Fixture(mode);
        var sessions = Enumerable.Range(0, QuizSessionRegistry.MaxActiveSessionsPerUser + 1)
            .Select(_ => fixture.Start("one,two")).ToArray();
        foreach (var session in sessions) fixture.Save(session);
        Assert.Null(fixture.Find(sessions[0].SessionId));
        foreach (var session in sessions.Skip(1)) Assert.Same(session, fixture.Find(session.SessionId));
        Assert.Null(fixture.Resume());
    }

    [Theory]
    [InlineData("typing")]
    [InlineData("flashcards")]
    public void RestartingSelectedWordsDoesNotResetTheGenericSession(string mode)
    {
        using var fixture = new Fixture(mode);
        var generic = fixture.Start(null);
        fixture.Save(generic);
        var result = fixture.Restart("one,two");
        Assert.Same(generic, fixture.Resume());
        Assert.Equal("one,two", result.RouteValues!["selectedWordIds"]);
        fixture.Restart(null);
        Assert.Null(fixture.Resume());
        Assert.Null(fixture.Find(generic.SessionId));
    }

    [Theory]
    [InlineData("typing")]
    [InlineData("flashcards")]
    public void RetrySubsetsDoNotMatchGenericPractice(string mode)
    {
        using var fixture = new Fixture(mode);
        var original = fixture.Start(null);
        fixture.Save(original);
        var retry = fixture.Retry(original);
        fixture.Save(retry);
        Assert.Same(original, fixture.Resume());
        fixture.Reset();
        Assert.Null(fixture.Resume());
        Assert.Same(retry, fixture.Find(retry.SessionId));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        private readonly TypingSessionService _typing;
        private readonly FlashcardSessionService _cards;
        private readonly string _mode;
        private readonly int _start;
        private readonly int _end;
        private readonly Guid _quiz = Guid.NewGuid();
        public Fixture(string mode, int start = 0, int end = 100)
        {
            _mode = mode; _start = start; _end = end;
            var registry = new QuizSessionRegistry(_cache);
            _typing = new TypingSessionService(_cache, null!, registry);
            _cards = new FlashcardSessionService(_cache, registry);
        }
        public IQuizSessionData Start(string? ids) => _mode == "typing"
            ? _typing.StartSession("user", _quiz, "Quiz", "English", "Polish", 2,
                [new() { Id = "one", Answer = "one" }, new() { Id = "two", Answer = "two" }],
                rangeStartPercent: _start, rangeEndPercent: _end, selectedWordIds: ids)
            : _cards.StartSession("user", _quiz, "Quiz", "English", "Polish", 2,
                [new() { Id = "one", Lemma = "one" }, new() { Id = "two", Lemma = "two" }],
                rangeStartPercent: _start, rangeEndPercent: _end, selectedWordIds: ids);
        public void Save(IQuizSessionData session)
        {
            if (session is TypingSessionData typed) _typing.SaveSession(typed);
            else _cards.SaveSession((FlashcardSessionData)session);
        }
        public IQuizSessionData? Resume() => _mode == "typing"
            ? _typing.FindResumableSession("user", _quiz, null, null, 2, _start, _end)
            : _cards.FindResumableSession("user", _quiz, null, null, 2, _start, _end);
        public IQuizSessionData? Find(string id) => _mode == "typing"
            ? _typing.FindSession(id, "user") : _cards.FindSession(id, "user");
        public void Reset()
        {
            if (_mode == "typing") _typing.ResetSession("user", _quiz, null, null, 2, _start, _end);
            else _cards.ResetSession("user", _quiz, null, null, 2, _start, _end);
        }
        public RedirectToActionResult Restart(string? ids) => Assert.IsType<RedirectToActionResult>(_mode == "typing"
            ? Authenticate(new TypingQuizController(null!, null!, _typing, null!)).Restart(_quiz, 2, selectedWordIds: ids)
            : Authenticate(new FlashcardQuizController(null!, null!, _cards, null!)).Restart(_quiz, 2, selectedWordIds: ids));
        public IQuizSessionData Retry(IQuizSessionData session)
        {
            if (session is FlashcardSessionData cards)
            {
                cards.AgainCards.AddRange(cards.Cards);
                var cardsController = Authenticate(new FlashcardQuizController(null!, null!, _cards, null!));
                var view = Assert.IsType<ViewResult>(cardsController.RestartAgain(cards.SessionId));
                var model = Assert.IsType<FlashcardQuizViewModel>(view.Model);
                return _cards.FindSession(model.SessionId, "user")!;
            }
            var typed = (TypingSessionData)session;
            typed.IncorrectWords.AddRange(typed.Words);
            var controller = Authenticate(new TypingQuizController(null!, null!, _typing, null!));
            var redirect = Assert.IsType<RedirectToActionResult>(controller.RestartIncorrect(typed.SessionId));
            return _typing.FindSession(Assert.IsType<string>(redirect.RouteValues!["sessionId"]), "user")!;
        }
        public void Dispose() => _cache.Dispose();
        private static T Authenticate<T>(T controller) where T : Controller
        {
            controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext
            { User = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "user")], "test")) } };
            return controller;
        }
    }
}
