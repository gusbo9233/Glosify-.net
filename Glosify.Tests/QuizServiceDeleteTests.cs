using Glosify.Data;
using Glosify.Services;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class QuizServiceDeleteTests
{
    [Fact]
    public async Task DeleteQuizAsync_DeletesQuizInsideCollectionWithoutDeletingCollection()
    {
        await using var context = CreateContext();
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            UserId = "user-1",
            Name = "Polish songs",
            Language = "Polish",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var quiz = CreateQuiz();
        quiz.CollectionId = collection.Id;
        context.AddRange(
            collection,
            quiz,
            new Word
            {
                Id = "word-1",
                QuizId = quiz.Id,
                Lemma = "słowo",
                Translation = "word"
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        var service = new QuizService(context, new TestLanguageContext());

        var deleted = await service.DeleteQuizAsync(quiz.Id, quiz.UserId);

        Assert.NotNull(deleted);
        Assert.Empty(context.Quizzes);
        Assert.Empty(context.Words);
        Assert.Equal(collection.Id, Assert.Single(context.Collections).Id);
    }

    [Fact]
    public async Task DeleteQuizAsync_KeepsAssistantHistoryAndClearsItsQuizContext()
    {
        await using var context = CreateContext();
        var quiz = CreateQuiz();
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            ContextQuizId = quiz.Id,
            UserId = quiz.UserId,
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
        var service = new QuizService(context, new TestLanguageContext());

        var deleted = await service.DeleteQuizAsync(quiz.Id, quiz.UserId);

        Assert.NotNull(deleted);
        Assert.Empty(context.Quizzes);
        Assert.Empty(context.Words);
        Assert.Null(Assert.Single(context.AssistantThreads).ContextQuizId);
        Assert.Null(Assert.Single(context.AssistantMessages).ContextQuizId);
    }

    [Fact]
    public async Task DeleteQuizAsync_DoesNotDeleteWordsWhenTheSaveFails()
    {
        await using var context = CreateContext();
        var quiz = CreateQuiz();
        context.AddRange(
            quiz,
            new Word
            {
                Id = "word-1",
                QuizId = quiz.Id,
                Lemma = "słowo",
                Translation = "word"
            });
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();
        context.FailWhenQuizIsDeleted = true;
        var service = new QuizService(context, new TestLanguageContext());

        await Assert.ThrowsAsync<DbUpdateException>(
            () => service.DeleteQuizAsync(quiz.Id, quiz.UserId));

        context.FailWhenQuizIsDeleted = false;
        context.ChangeTracker.Clear();
        Assert.True(await context.Quizzes.AnyAsync(item => item.Id == quiz.Id));
        Assert.True(await context.Words.AnyAsync(item => item.QuizId == quiz.Id));
    }

    private static TestGlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new TestGlosifyContext(options);
    }

    private static Quiz CreateQuiz() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Polish",
        UserId = "user-1",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        ProcessingStatus = "Ready",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private sealed class TestGlosifyContext(DbContextOptions<GlosifyContext> options)
        : GlosifyContext(options)
    {
        public bool FailWhenQuizIsDeleted { get; set; }

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            if (FailWhenQuizIsDeleted
                && ChangeTracker.Entries<Quiz>().Any(entry => entry.State == EntityState.Deleted))
            {
                throw new DbUpdateException("Simulated quiz delete failure.");
            }

            return base.SaveChangesAsync(cancellationToken);
        }
    }

    private sealed class TestLanguageContext : ILanguageContext
    {
        public string? CurrentLanguage => "Polish";
        public bool HasLanguage => true;
        public IReadOnlyList<string> SupportedLanguages { get; } = ["Polish"];
        public bool TrySetLanguage(string language) => true;
        public void Clear() { }
    }
}
