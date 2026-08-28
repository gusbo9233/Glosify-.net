using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Quizzes;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Glosify.Tests;

public class ChangeApplierTests
{
    private static readonly DateTimeOffset SeededAt =
        new(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Workflow_rolls_back_quiz_and_keeps_proposal_active_when_a_later_change_fails()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Must roll back",
                source_language = "English",
                target_language = "Polish",
            })),
            new PendingChange(PendingChangeKinds.CreateCollection, JsonSerializer.SerializeToElement(new
            {
                name = "Child",
                language = "Polish",
                parent_collection_id = Guid.NewGuid(),
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<CollectionParentNotFoundException>(
            () => workflow.ApplyAsync(messageId, "user-1", CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Quizzes.ToListAsync());
        Assert.Equal(
            AssistantMessageStatus.Active,
            (await db.AssistantMessages.SingleAsync(message => message.Id == messageId)).Status);
    }

    [Fact]
    public async Task Workflow_commits_creation_and_second_apply_does_not_duplicate_it()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Travel",
                source_language = "English",
                target_language = "Polish",
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        var first = await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);
        var second = await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        Assert.Equal(1, first.Applied);
        Assert.Equal("Travel", first.CreatedQuiz?.Name);
        Assert.Equal(0, second.Applied);
        Assert.Single(await db.Quizzes.ToListAsync());
        Assert.Equal(
            AssistantMessageStatus.Applied,
            (await db.AssistantMessages.SingleAsync(message => message.Id == messageId)).Status);
    }

    [Fact]
    public async Task Workflow_rejects_an_entire_stale_custom_quiz_batch_without_partial_application()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateCollection, JsonSerializer.SerializeToElement(new
            {
                name = "Must not apply",
                language = "Polish",
            })),
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Legacy shell",
                source_language = "English",
                target_language = "Polish",
                custom_quiz = new { name = "Retired exercise" },
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(SeededAt));

        var result = await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        Assert.Equal(0, result.Applied);
        Assert.Empty(await db.Collections.ToListAsync());
        Assert.Empty(await db.Quizzes.ToListAsync());
        Assert.Equal(
            AssistantMessageStatus.Rejected,
            (await db.AssistantMessages.SingleAsync(message => message.Id == messageId)).Status);
    }

    [Fact]
    public async Task ApplyAsync_PropagatesAStaleCollectionAndCreatesNothing()
    {
        await using var db = CreateContext();
        var change = new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
        {
            name = "Travel",
            source_language = "English",
            target_language = "Polish",
            collection_id = Guid.NewGuid(),
        }));

        await Assert.ThrowsAsync<QuizCollectionNotFoundException>(
            () => CreateApplier(db).ApplyAsync(null, "user-1", [change], CancellationToken.None));

        Assert.Empty(await db.Quizzes.ToListAsync());
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

    // A standard quiz proposed with both content types has to arrive whole. Words and
    // sentences are staged by different helpers, so nothing but a real transaction proves
    // they commit as one unit.
    [Fact]
    public async Task ApplyCreateQuiz_PersistsStarterWordsAndSentencesInOneTransaction()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Travel Polish",
                source_language = "English",
                target_language = "Polish",
                words = new[] { new { word = "pociag", translation = "train" } },
                sentences = new[]
                {
                    new { text = "Pociag odjezdza o osmej.", translation = "The train leaves at eight." },
                    new { text = "To jest moj dom.", translation = "This is my house." },
                },
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        var result = await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        db.ChangeTracker.Clear();
        var quiz = Assert.Single(await db.Quizzes.ToListAsync());
        Assert.Equal("Travel Polish", result.CreatedQuiz?.Name);
        var word = Assert.Single(await db.Words.Where(w => w.QuizId == quiz.Id).ToListAsync());
        Assert.Equal("pociag", word.Lemma);
        var sentences = await db.QuizSentences
            .Where(sentence => sentence.QuizId == quiz.Id)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync();
        Assert.Equal(2, sentences.Count);
        Assert.Equal("Pociag odjezdza o osmej.", sentences[0].Text);
        Assert.Equal("The train leaves at eight.", sentences[0].Translation);
    }

    // A stored proposal can be applied long after it was built, so the durable boundary
    // filters the overlap too rather than trusting the tool to have caught it.
    [Fact]
    public async Task ApplyCreateQuiz_DoesNotPersistASentenceAsAWordToo()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Travel Polish",
                source_language = "English",
                target_language = "Polish",
                words = new[]
                {
                    new { word = "dom", translation = "house" },
                    new { word = "To jest moj dom.", translation = "This is my house." },
                },
                sentences = new[]
                {
                    new { text = "To jest moj dom.", translation = "This is my house." },
                },
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        db.ChangeTracker.Clear();
        var word = Assert.Single(await db.Words.ToListAsync());
        Assert.Equal("dom", word.Lemma);
        var sentence = Assert.Single(await db.QuizSentences.ToListAsync());
        Assert.Equal("To jest moj dom.", sentence.Text);
    }

    // The tool-level check only sees sentences queued before the word. Apply is what makes the
    // outcome independent of the order the model made its calls in.
    // The tool-level check only sees sentences queued before the word. Apply is what makes the
    // outcome independent of the order the model made its calls in.
    [Fact]
    public async Task ApplyAsync_AddWord_IsSkippedWhenTheSameProposalAddsItAsASentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        // Word first: the tool could not have known a sentence was coming.
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.AddWord,
                JsonSerializer.SerializeToElement(new { word = "To jest moj dom.", translation = "This is my house." })),
            new PendingChange(
                PendingChangeKinds.AddSentence,
                JsonSerializer.SerializeToElement(new { text = "To jest moj dom.", translation = "This is my house." })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Empty(db.Words.Where(word => word.QuizId == quizId));
        Assert.Single(db.QuizSentences.Where(sentence => sentence.QuizId == quizId));
    }

    // A sentence stored by an earlier turn counts too, not just one in the same proposal.
    [Fact]
    public async Task ApplyAsync_AddWord_IsSkippedWhenTheQuizAlreadyHasItAsASentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = "To jest moj dom.",
            Translation = "This is my house.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.AddWord,
                JsonSerializer.SerializeToElement(new { word = "To jest moj dom", translation = "This is my house." })),
            new PendingChange(
                PendingChangeKinds.AddWord,
                JsonSerializer.SerializeToElement(new { word = "dom", translation = "house" })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(1, result.Applied);
        var word = Assert.Single(db.Words.Where(item => item.QuizId == quizId));
        Assert.Equal("dom", word.Lemma);
    }

    // "Delete that sentence and add it as vocabulary instead" is an ordinary request. Judging
    // the word against the starting state would silently drop it.
    [Fact]
    public async Task ApplyAsync_AddWord_IsAllowedWhenTheSameProposalDeletesThatSentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "To jest moj dom.",
            Translation = "This is my house.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.DeleteSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = sentenceId })),
            new PendingChange(
                PendingChangeKinds.AddWord,
                JsonSerializer.SerializeToElement(new { word = "To jest moj dom.", translation = "This is my house." })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(2, result.Applied);
        var word = Assert.Single(db.Words.Where(item => item.QuizId == quizId));
        Assert.Equal("To jest moj dom.", word.Lemma);
        Assert.Empty(db.QuizSentences.Where(item => item.QuizId == quizId));
    }

    // The mirror case: a sentence the proposal edits into existence must block the word.
    [Fact]
    public async Task ApplyAsync_AddWord_IsSkippedWhenAnEditProducesThatSentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Stary tekst.",
            Translation = "Old text.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.EditSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = sentenceId, text = "To jest moj dom." })),
            new PendingChange(
                PendingChangeKinds.AddWord,
                JsonSerializer.SerializeToElement(new { word = "To jest moj dom.", translation = "This is my house." })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Empty(db.Words.Where(item => item.QuizId == quizId));
    }

    // Deleting a sentence and adding it back in the same turn — to fix its translation — was
    // refused as a duplicate of the row just deleted, so the sentence disappeared entirely.
    [Fact]
    public async Task ApplyAsync_DeletingAndReAddingASentenceInOneProposalKeepsIt()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "To jest moj dom.",
            Translation = "Bad translation.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.DeleteSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = sentenceId })),
            new PendingChange(
                PendingChangeKinds.AddSentence,
                JsonSerializer.SerializeToElement(new { text = "To jest moj dom.", translation = "This is my house." })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(2, result.Applied);
        var sentence = Assert.Single(db.QuizSentences.Where(item => item.QuizId == quizId));
        Assert.Equal("This is my house.", sentence.Translation);
    }

    // Editing a sentence away from a text left that text claimed, so adding it back later in
    // the same proposal was refused by a row that no longer holds it.
    [Fact]
    public async Task ApplyAsync_EditingASentenceFreesTheTextItNoLongerHolds()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var sentenceId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = sentenceId,
            QuizId = quizId,
            Text = "Stary tekst.",
            Translation = "Old text.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.EditSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = sentenceId, text = "Nowy tekst." })),
            new PendingChange(
                PendingChangeKinds.AddSentence,
                JsonSerializer.SerializeToElement(new { text = "Stary tekst.", translation = "Old text, kept." })),
        };

        var result = await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        Assert.Equal(2, result.Applied);
        var texts = db.QuizSentences.Where(item => item.QuizId == quizId)
            .Select(item => item.Text).OrderBy(text => text).ToArray();
        Assert.Equal(["Nowy tekst.", "Stary tekst."], texts);
    }

    // A staged insert is not among the loaded rows, so releasing a text has to account for it
    // or the same sentence can be inserted twice.
    [Fact]
    public async Task ApplyAsync_AStagedSentenceStillClaimsItsTextWhenAnotherRowIsDeleted()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = firstId,
            QuizId = quizId,
            Text = "Do usuniecia.",
            Translation = "To delete.",
            CreatedAt = SeededAt,
        });
        db.QuizSentences.Add(new QuizSentence
        {
            Id = secondId,
            QuizId = quizId,
            Text = "Druga.",
            Translation = "Second.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var changes = new[]
        {
            new PendingChange(
                PendingChangeKinds.DeleteSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = firstId })),
            new PendingChange(
                PendingChangeKinds.AddSentence,
                JsonSerializer.SerializeToElement(new { text = "Do usuniecia.", translation = "Re-added." })),
            // Retargets the second row onto the staged text, then deletes it. The staged insert
            // must keep the text claimed so the final add cannot duplicate it.
            new PendingChange(
                PendingChangeKinds.EditSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = secondId, text = "Do usuniecia." })),
            new PendingChange(
                PendingChangeKinds.DeleteSentence,
                JsonSerializer.SerializeToElement(new { sentence_id = secondId })),
            new PendingChange(
                PendingChangeKinds.AddSentence,
                JsonSerializer.SerializeToElement(new { text = "Do usuniecia.", translation = "Duplicate." })),
        };

        await applier.ApplyAsync(quizId, "user-1", changes, CancellationToken.None);

        var sentence = Assert.Single(db.QuizSentences.Where(item => item.QuizId == quizId));
        Assert.Equal("Re-added.", sentence.Translation);
    }

    // Ordinary vocabulary must keep working; the check only fires on an actual sentence match.
    [Fact]
    public async Task ApplyAsync_AddWord_StillAddsAPhraseThatIsNotAStoredSentence()
    {
        await using var db = CreateContext();
        var quizId = Guid.NewGuid();
        db.Quizzes.Add(CreateQuiz(quizId, "user-1"));
        db.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = "By the way, I am late.",
            Translation = "Nawiasem mowiac, jestem spozniony.",
            CreatedAt = SeededAt,
        });
        await db.SaveChangesAsync();
        var applier = CreateApplier(db);
        var change = new PendingChange(
            PendingChangeKinds.AddWord,
            JsonSerializer.SerializeToElement(new { word = "by the way", translation = "nawiasem mowiac" }));

        var result = await applier.ApplyAsync(quizId, "user-1", [change], CancellationToken.None);

        Assert.Equal(1, result.Applied);
        Assert.Single(db.Words.Where(item => item.QuizId == quizId));
    }

    // A sentence missing its translation is never stored, so it must not displace a matching
    // word either — otherwise the content disappears from both tables.
    [Fact]
    public async Task ApplyCreateQuiz_KeepsAWordWhoseMatchingSentenceIsNotPersistable()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Travel Polish",
                source_language = "English",
                target_language = "Polish",
                words = new[] { new { word = "To jest moj dom.", translation = "This is my house." } },
                sentences = new[] { new { text = "To jest moj dom.", translation = "   " } },
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        db.ChangeTracker.Clear();
        Assert.Empty(await db.QuizSentences.ToListAsync());
        var word = Assert.Single(await db.Words.ToListAsync());
        Assert.Equal("To jest moj dom.", word.Lemma);
    }

    [Fact]
    public async Task ApplyCreateQuiz_DeduplicatesStarterSentenceText()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Travel Polish",
                source_language = "English",
                target_language = "Polish",
                sentences = new[]
                {
                    new { text = "To jest dom.", translation = "This is a house." },
                    new { text = "to jest dom.", translation = "This is a house." },
                    new { text = "  ", translation = "Blank text is skipped." },
                },
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        await workflow.ApplyAsync(messageId, "user-1", CancellationToken.None);

        db.ChangeTracker.Clear();
        var sentence = Assert.Single(await db.QuizSentences.ToListAsync());
        Assert.Equal("To jest dom.", sentence.Text);
    }

    // The quiz row is created by a service call and the sentences are staged afterwards, so a
    // failure later in the proposal must not leave a quiz holding only half its content.
    [Fact]
    public async Task ApplyCreateQuiz_RollsBackQuizWordsAndSentencesTogether()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var db = new SqliteGlosifyContext(options);
        await db.Database.EnsureCreatedAsync();
        var messageId = await SeedProposalAsync(db,
        [
            new PendingChange(PendingChangeKinds.CreateQuiz, JsonSerializer.SerializeToElement(new
            {
                name = "Must roll back",
                source_language = "English",
                target_language = "Polish",
                words = new[] { new { word = "dom", translation = "house" } },
                sentences = new[] { new { text = "To jest dom.", translation = "This is a house." } },
            })),
            new PendingChange(PendingChangeKinds.CreateCollection, JsonSerializer.SerializeToElement(new
            {
                name = "Child",
                language = "Polish",
                parent_collection_id = Guid.NewGuid(),
            })),
        ]);
        var workflow = new AssistantChangeWorkflow(
            db,
            CreateApplier(db),
            new AssistantMessagePresenter(),
            null!,
            new FakeTimeProvider(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));

        await Assert.ThrowsAsync<CollectionParentNotFoundException>(
            () => workflow.ApplyAsync(messageId, "user-1", CancellationToken.None));

        db.ChangeTracker.Clear();
        Assert.Empty(await db.Quizzes.ToListAsync());
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Empty(await db.QuizSentences.ToListAsync());
    }

    private static ChangeApplier CreateApplier(GlosifyContext db)
    {
        var anki = new Glosify.Services.Anki.AnkiCollectionService(db, new FakeTimeProvider(SeededAt));
        return new ChangeApplier(
            db,
            new QuizService(db, null!, anki),
            new CollectionService(db, anki),
            NullLogger<ChangeApplier>.Instance,
            anki);
    }

    private static async Task<Guid> SeedProposalAsync(
        GlosifyContext db,
        IReadOnlyList<PendingChange> changes)
    {
        var threadId = Guid.NewGuid();
        var messageId = Guid.NewGuid();
        db.Users.Add(new ApplicationUser
        {
            Id = "user-1",
            UserName = "user-1",
            NormalizedUserName = "USER-1",
        });
        db.AssistantThreads.Add(new AssistantThread
        {
            Id = threadId,
            UserId = "user-1",
            Title = "Apply test",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        db.AssistantMessages.Add(new AssistantMessage
        {
            Id = messageId,
            ThreadId = threadId,
            Sequence = 0,
            Role = AssistantMessageRole.Model,
            ContentJson = "{\"parts\":[]}",
            PendingChangesJson = JsonSerializer.Serialize(changes),
            Status = AssistantMessageStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
        });
        await db.SaveChangesAsync();
        return messageId;
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
