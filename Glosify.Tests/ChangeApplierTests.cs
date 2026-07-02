using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

public class ChangeApplierTests
{
    [Fact]
    public async Task ApplyAsync_DeleteSentence_RemovesSentenceFromQuiz()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Idę do domu.",
            Translation = "I am going home.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var change = new PendingChange(
            PendingChangeKinds.DeleteSentence,
            JsonSerializer.SerializeToElement(new { sentence_id = sentenceId, text = "Idę do domu." }));

        var result = await applier.ApplyAsync(quizId, "user-1", [change], CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Empty(db.QuizSentences.Where(s => s.QuizId == quizId));
    }

    [Fact]
    public async Task ApplyAsync_DeleteSentence_IgnoresSentenceFromOtherQuiz()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var otherQuizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = otherQuizId,
            Text = "Idę do domu.",
            Translation = "I am going home.",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var change = new PendingChange(
            PendingChangeKinds.DeleteSentence,
            JsonSerializer.SerializeToElement(new { sentence_id = sentenceId, text = "Idę do domu." }));

        var result = await applier.ApplyAsync(quizId, "user-1", [change], CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Single(db.QuizSentences.Where(s => s.QuizId == otherQuizId));
    }

    [Fact]
    public async Task ApplyAsync_AddWord_DeduplicatesCaseInsensitively()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.Words.Add(new Word { Id = "w1", QuizId = quizId, Lemma = "Haus", Translation = "house" });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var change = new PendingChange(
            PendingChangeKinds.AddWord,
            JsonSerializer.SerializeToElement(new { word = "haus", translation = "house" }));

        var result = await applier.ApplyAsync(quizId, "user-1", [change], CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Single(db.Words.Where(w => w.QuizId == quizId));
    }

    [Fact]
    public async Task ApplyAsync_EditSentence_UpdatesOnlySentenceInCurrentQuiz()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Idę dom.",
            Translation = "I go home.",
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var change = new PendingChange(
            PendingChangeKinds.EditSentence,
            JsonSerializer.SerializeToElement(new
            {
                sentence_id = sentenceId,
                text = "Idę do domu.",
                translation = "I am going home.",
            }));

        var result = await applier.ApplyAsync(quizId, "user-1", [change], CancellationToken.None);

        Assert.Equal(1, result.Applied);
        var sentence = await db.QuizSentences.SingleAsync(s => s.Id == sentenceId);
        Assert.Equal("Idę do domu.", sentence.Text);
        Assert.Equal("I am going home.", sentence.Translation);
    }

    [Fact]
    public async Task ApplyAsync_LibraryOrganizationChanges_UseOwnedCollections()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        var destinationId = Guid.NewGuid();
        var quiz = CreateQuiz(quizId, "user-1");
        quiz.CollectionId = sourceId;
        db.Collections.AddRange(
            new Collection
            {
                Id = sourceId,
                UserId = "user-1",
                Name = "Basics",
                Language = "Polish",
            },
            new Collection
            {
                Id = destinationId,
                UserId = "user-1",
                Name = "Course",
                Language = "Polish",
            });
        db.Quizzes.Add(quiz);
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.MoveQuiz,
                JsonSerializer.SerializeToElement(new
                {
                    quiz_id = quizId,
                    collection_id = destinationId,
                })),
            new PendingChange(
                PendingChangeKinds.RenameCollection,
                JsonSerializer.SerializeToElement(new
                {
                    collection_id = sourceId,
                    name = "Foundations",
                })),
            new PendingChange(
                PendingChangeKinds.MoveCollection,
                JsonSerializer.SerializeToElement(new
                {
                    collection_id = sourceId,
                    parent_collection_id = destinationId,
                })),
        };

        var result = await applier.ApplyAsync(null, "user-1", changes, CancellationToken.None);

        Assert.Equal(3, result.Applied);
        Assert.Equal(destinationId, (await db.Quizzes.SingleAsync(q => q.Id == quizId)).CollectionId);
        var source = await db.Collections.SingleAsync(c => c.Id == sourceId);
        Assert.Equal("Foundations", source.Name);
        Assert.Equal(destinationId, source.ParentCollectionId);
    }

    private static ChangeApplier CreateApplier(GlosifyContext db)
    {
        return new ChangeApplier(
            db,
            null!,
            new CollectionService(db),
            NullLogger<ChangeApplier>.Instance);
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }

    private static Quiz CreateQuiz(Guid id, string userId) => new()
    {
        Id = id,
        UserId = userId,
        Name = "Polish",
        SourceLanguage = "English",
        TargetLanguage = "Polish",
        Language = "Polish",
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
