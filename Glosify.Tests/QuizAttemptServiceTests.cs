using Glosify.Data;
using Glosify.Services.Flashcards;
using Glosify.Services.Quizzes;
using Glosify.Services.Typing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class QuizAttemptServiceTests
{
    private const string UserId = "user-1";

    [Fact]
    public async Task RecordFlashcardAttemptAsync_MapsSessionCounts()
    {
        await using var context = CreateContext();
        var service = new QuizAttemptService(context);
        var quizId = Guid.NewGuid();
        var classroomId = Guid.NewGuid();
        var session = new FlashcardSessionData
        {
            SessionId = "session-1",
            UserId = UserId,
            QuizId = quizId,
            ClassroomId = classroomId,
            PracticeDirection = PracticeDirection.SourceToTarget,
            PracticeItemType = PracticeItemType.Words,
            RememberedCount = 7,
            AgainCount = 2,
            SkippedCount = 1,
            Cards =
            [
                .. Enumerable.Range(0, 10).Select(i => new FlashcardCardData { Id = $"card-{i}" })
            ]
        };

        await service.RecordFlashcardAttemptAsync(session);

        var attempt = Assert.Single(context.QuizAttempts.ToList());
        Assert.Equal(quizId, attempt.QuizId);
        Assert.Equal(UserId, attempt.UserId);
        Assert.Equal(classroomId, attempt.ClassroomId);
        Assert.Equal("flashcards", attempt.Mode);
        Assert.Equal(10, attempt.TotalItems);
        Assert.Equal(7, attempt.CorrectCount);
        Assert.Equal(2, attempt.IncorrectCount);
        Assert.Equal(1, attempt.SkippedCount);
        Assert.Empty(context.QuizAttemptItems.ToList());
    }

    [Fact]
    public async Task RecordTypingAttemptAsync_MapsItemsWithCorrectness()
    {
        await using var context = CreateContext();
        var service = new QuizAttemptService(context);
        var words = new List<TypingWordData>
        {
            new() { Id = "w1", Prompt = "dog", Answer = "perro" },
            new() { Id = "w2", Prompt = "cat", Answer = "gato" },
            new() { Id = "w3", Prompt = "bird", Answer = "pájaro" }
        };
        var session = new TypingSessionData
        {
            SessionId = "session-1",
            UserId = UserId,
            QuizId = Guid.NewGuid(),
            CorrectCount = 2,
            IncorrectCount = 1,
            Words = words,
            IncorrectWords = { words[1] }
        };

        await service.RecordTypingAttemptAsync(session);

        var attempt = Assert.Single(context.QuizAttempts.ToList());
        Assert.Equal("typing", attempt.Mode);
        Assert.Null(attempt.ClassroomId);
        Assert.Equal(3, attempt.TotalItems);

        var items = context.QuizAttemptItems.OrderBy(i => i.Sequence).ToList();
        Assert.Equal(3, items.Count);
        Assert.True(items[0].IsCorrect);
        Assert.False(items[1].IsCorrect);
        Assert.Equal("cat", items[1].Prompt);
        Assert.Equal("gato", items[1].ExpectedAnswer);
        Assert.True(items[2].IsCorrect);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}
