using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.CustomQuizzes;
using Microsoft.Data.Sqlite;

namespace Glosify.Tests;

public class ChangeApplierTests
{
    [Fact]
    public async Task ApplyAsync_RollsBackAllChanges_WhenLaterCustomQuizChangeFails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseSqlite(connection)
            .Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        db.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "user-1",
            NormalizedUserName = "USER-1",
        });
        await db.SaveChangesAsync();

        const string draftRef = "rollback-draft";
        var changes = new[]
        {
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Must roll back",
                source_language = "English",
                target_language = "Polish",
                words = new[] { new { word = "dom", translation = "house" } },
                custom_quiz = new { name = "Atomic", draft_ref = draftRef },
            })),
            AtomicElement(draftRef, new
            {
                id = "duplicate",
                type = "text_input",
                label = "Answer",
                expected_text = "dom",
            }),
            AtomicElement(draftRef, new
            {
                id = "duplicate",
                type = "instruction_label",
                text = "This duplicate id makes the document invalid.",
            }),
        };

        await Assert.ThrowsAsync<CustomQuizValidationException>(
            () => CreateApplier(db).ApplyAsync(null, "user-1", changes, CancellationToken.None));

        Assert.Empty(await db.Quizzes.ToListAsync());
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Empty(await db.CustomQuizzes.ToListAsync());
    }

    [Fact]
    public async Task ApplyAsync_ResolvesAtomicElementsAgainstPendingCustomQuizShell()
    {
        await using var db = CreateContext();
        const string draftRef = "custom-draft-1";
        var changes = new[]
        {
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Verb source",
                source_language = "English",
                target_language = "Polish",
                words = new[] { new { word = "być", translation = "to be" } },
                custom_quiz = new { name = "Verb endings", draft_ref = draftRef },
            })),
            AtomicElement(draftRef, new { id = "answer", type = "text_input", label = "Complete: ja będ___", expected_text = "ę" }),
            AtomicElement(draftRef, new { id = "submit", type = "submit_button", text = "Check" }),
            AtomicElement(draftRef, new { id = "feedback", type = "feedback_message" }),
        };

        var result = await CreateApplier(db).ApplyAsync(null, "user-1", changes, CancellationToken.None);
        var custom = await new CustomQuizService(db).GetForEditorAsync(result.CreatedCustomQuizId!.Value, "user-1");

        Assert.Equal(4, result.Applied);
        Assert.True(custom!.IsPlayable);
        Assert.Equal("ę", custom.Document.Blocks.Single(block => block.Id == "answer").ExpectedText);
        Assert.Equal(3, custom.Document.Blocks.Count);
    }

    [Fact]
    public async Task ApplyAsync_CreateQuizWithCustomQuiz_ResolvesStarterWordBindings()
    {
        await using var db = CreateContext();
        var change = new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
        {
            name = "Book page",
            source_language = "English",
            target_language = "Polish",
            words = new[] { new { word = "dom", translation = "house" } },
            custom_quiz = new
            {
                name = "Book page practice",
                blocks = new object[]
                {
                    new { id = "answer", type = "text_input", label = "Translate house", expected_binding = new { word = "dom", field = "lemma" } },
                    new { id = "submit", type = "submit_button", text = "Check" },
                    new { id = "feedback", type = "feedback_message" },
                }
            }
        }));

        var result = await CreateApplier(db).ApplyAsync(null, "user-1", [change], CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.NotNull(result.CreatedQuizId);
        Assert.NotNull(result.CreatedCustomQuizId);
        var word = await db.Words.SingleAsync(item => item.QuizId == result.CreatedQuizId);
        var custom = await new CustomQuizService(db).GetForEditorAsync(result.CreatedCustomQuizId!.Value, "user-1");
        Assert.True(custom!.IsPlayable);
        Assert.Equal(word.Id, custom.Document.Blocks.Single(block => block.Id == "answer").ExpectedBinding!.WordId);
    }

    private static PendingChange AtomicElement(string draftRef, object block) =>
        new(PendingChangeKinds.AddCustomQuizElement, JsonSerializer.SerializeToElement(new
        {
            custom_quiz_ref = draftRef,
            block,
        }));

    [Fact]
    public async Task ApplyAsync_CustomQuizElementChanges_AddConfigureAndRemoveElements()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.Words.Add(new Word { Id = "w1", QuizId = quizId, Lemma = "dom", Translation = "house" });
        await db.SaveChangesAsync();
        var service = new CustomQuizService(db);
        var custom = await service.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quizId,
            Name = "Builder",
            Document = new CustomQuizDocumentV1
            {
                Blocks =
                [
                    new() { Id = "answer", Type = CustomQuizBlockTypes.TextInput, Label = "Old label", ExpectedBinding = new() { WordId = "w1", Field = "lemma" } },
                    new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton },
                    new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage },
                ]
            }
        }, "user-1");
        var changes = new[]
        {
            new PendingChange(PendingChangeKinds.AddCustomQuizElements, JsonSerializer.SerializeToElement(new
            {
                custom_quiz_id = custom.Id,
                blocks = new[] { new { id = "instructions", type = "instruction_label", text = "Answer carefully." } },
            })),
            new PendingChange(PendingChangeKinds.ConfigureCustomQuizElement, JsonSerializer.SerializeToElement(new
            {
                custom_quiz_id = custom.Id,
                block_id = "answer",
                settings = new { label = "Type the Polish word" },
            })),
            new PendingChange(PendingChangeKinds.RemoveCustomQuizElement, JsonSerializer.SerializeToElement(new
            {
                custom_quiz_id = custom.Id,
                block_id = "instructions",
            })),
        };

        var result = await CreateApplier(db).ApplyAsync(quizId, "user-1", changes, CancellationToken.None);
        var updated = await service.GetForEditorAsync(custom.Id!.Value, "user-1");

        Assert.Equal(3, result.Applied);
        Assert.Equal("Type the Polish word", updated!.Document.Blocks.Single(block => block.Id == "answer").Label);
        Assert.DoesNotContain(updated.Document.Blocks, block => block.Id == "instructions");
        Assert.True(updated.IsPlayable);
    }

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
    public async Task ApplyAsync_DeleteWord_PrunesCustomQuizBindings()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.Words.Add(new Word { Id = "w1", QuizId = quizId, Lemma = "kawa", Translation = "coffee" });
        await db.SaveChangesAsync();
        var customService = new CustomQuizService(db);
        var custom = await customService.CreateAsync(new SaveCustomQuizRequest
        {
            QuizId = quizId,
            Name = "Coffee",
            Document = new CustomQuizDocumentV1
            {
                Blocks =
                [
                    new() { Id = "answer", Type = CustomQuizBlockTypes.TextInput, Order = 0, ColumnSpan = 12, Label = "Answer", ExpectedBinding = new() { WordId = "w1", Field = "lemma" } },
                    new() { Id = "submit", Type = CustomQuizBlockTypes.SubmitButton, Order = 1, ColumnSpan = 6 },
                    new() { Id = "feedback", Type = CustomQuizBlockTypes.FeedbackMessage, Order = 2, ColumnSpan = 6 }
                ]
            }
        }, "user-1");
        var change = new PendingChange(PendingChangeKinds.DeleteWord, JsonSerializer.SerializeToElement(new { word_id = "w1" }));

        var result = await CreateApplier(db).ApplyAsync(quizId, "user-1", [change], CancellationToken.None);
        var editor = await customService.GetForEditorAsync(custom.Id!.Value, "user-1");

        Assert.Equal(1, result.Applied);
        Assert.DoesNotContain(editor!.Document.Blocks, block => block.Id == "answer");
        Assert.False(editor.IsPlayable);
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
            new QuizService(db, null!),
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

    private sealed class SqliteGlosifyContext(DbContextOptions<GlosifyContext> options)
        : GlosifyContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // SQL Server generates rowversion values. SQLite does not, so keep the
            // concurrency token but write the entity's initialized byte array.
            modelBuilder.Entity<CustomQuiz>()
                .Property(item => item.RowVersion)
                .IsConcurrencyToken()
                .ValueGeneratedNever();
        }
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
