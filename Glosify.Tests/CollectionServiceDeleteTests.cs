using Glosify.Data;
using Glosify.Services.Anki;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public class CollectionServiceDeleteTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 15, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task DeleteCollectionTreeAsync_KeepsAssistantHistoryAndClearsItsQuizContext()
    {
        await using var context = CreateContext();
        var root = CreateCollection("Polish course");
        var child = CreateCollection("Chapter 1");
        child.ParentCollectionId = root.Id;
        var quiz = new Quiz
        {
            Id = Guid.NewGuid(),
            Name = "Chapter 1 words",
            UserId = UserId,
            CollectionId = child.Id,
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
            ProcessingStatus = "Ready",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            ContextQuizId = quiz.Id,
            UserId = UserId,
            Title = "Quiz chat",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        var message = new AssistantMessage
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            ContextQuizId = quiz.Id,
            Sequence = 0,
            Role = AssistantMessageRole.User,
            ContentJson = """{"parts":[]}""",
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow
        };
        context.AddRange(
            root,
            child,
            quiz,
            new Word
            {
                Id = "word-1",
                QuizId = quiz.Id,
                Lemma = "słowo",
                Translation = "word"
            },
            thread,
            message);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var anki = new AnkiCollectionService(context, new FakeTimeProvider(Now));
        var deck = await anki.CreateAsync(new("Chapter deck", "English", "Polish", "UTC"), UserId);
        Assert.True(await anki.AddQuizAsync(new(deck.Id, quiz.Id, true, false, false, false), UserId));
        Assert.Single(await context.AnkiCards.Where(card => card.IsActive).ToListAsync());
        var service = new CollectionService(context, anki);

        var deleted = await service.DeleteCollectionTreeAsync(root.Id, UserId);

        Assert.True(deleted);
        Assert.Empty(context.Collections);
        Assert.Empty(context.Quizzes);
        Assert.Empty(context.Words);
        Assert.Null(Assert.Single(context.AssistantThreads).ContextQuizId);
        Assert.Null(Assert.Single(context.AssistantMessages).ContextQuizId);
        Assert.Empty(await context.AnkiQuizLinks.ToListAsync());
        Assert.False(Assert.Single(await context.AnkiNotes.ToListAsync()).IsActive);
        Assert.All(await context.AnkiCards.ToListAsync(), card => Assert.False(card.IsActive));
    }

    [Fact]
    public async Task DeleteCollectionTreeAsync_DoesNotTouchOtherUsersCollections()
    {
        await using var context = CreateContext();
        var collection = CreateCollection("Polish course");
        context.Add(collection);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new CollectionService(context, new AnkiCollectionService(context, new FakeTimeProvider(Now)));

        var deleted = await service.DeleteCollectionTreeAsync(collection.Id, "someone-else");

        Assert.False(deleted);
        Assert.Single(context.Collections);
    }

    private const string UserId = "user-1";

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Collection CreateCollection(string name) => new()
    {
        Id = Guid.NewGuid(),
        UserId = UserId,
        Name = name,
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow
    };
}
