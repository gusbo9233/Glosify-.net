using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AnkiWorkspaceTests
{
    [Fact]
    public async Task Picker_shows_all_selected_quiz_items_then_existing_membership()
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await VerifyAsync(db);
    }

    [SqlServerFact]
    public Task SqlServer_picker_shows_all_selected_quiz_items_then_existing_membership() =>
        SqlServerTestDatabase.RunAsync("anki-picker", VerifyAsync);

    private static async Task VerifyAsync(GlosifyContext db)
    {
        const string userId = "picker-user";
        db.Users.Add(new ApplicationUser { Id = userId, UserName = "picker@example.test" });
        var quiz = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Polish basics", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        var other = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Spanish", SourceLanguage = "English", TargetLanguage = "Spanish", Language = "Spanish" };
        db.AddRange(quiz, other);
        var created = DateTimeOffset.UtcNow;
        for (var i = 0; i < 105; i++)
            db.Words.Add(new Word { Id = $"word-{i:D3}", QuizId = quiz.Id, Lemma = $"target-{i:D3}", Translation = $"source-{i:D3}", CreatedAt = created });
        db.Words.Add(new Word { Id = "other-language", QuizId = other.Id, Lemma = "target-104", Translation = "other" });
        var sentenceId = Guid.NewGuid();
        db.QuizSentences.Add(new QuizSentence { Id = sentenceId, QuizId = quiz.Id, Text = "sentence target", Translation = "sentence meaning", CreatedAt = created });
        await db.SaveChangesAsync();
        var service = new AnkiCollectionService(db, TimeProvider.System);
        await service.AddItemForLanguageAsync("Polish", "UTC", new(quiz.Id, "words", "word-104"), userId);
        db.ChangeTracker.Clear();

        var first = await service.GetWorkspaceAsync(userId, "Polish");
        Assert.Equal(106, first.AvailableItems.Count);
        Assert.Equal(quiz.Id, first.SelectedQuizId);
        Assert.True(first.HasCards);
        Assert.Equal(new[] { "English" }, first.SourceLanguages);
        Assert.Single(first.AvailableItems, item => item.InAnki);
        Assert.True(first.AvailableItems.Single(item => item.ItemId == "word-104").InAnki);
        Assert.Equal(sentenceId.ToString(), first.AvailableItems.Last().ItemId);
        var found = await service.GetWorkspaceAsync(userId, "Polish", search: "target-104");
        Assert.Equal("word-104", Assert.Single(found.AvailableItems).ItemId);
        Assert.True(found.AvailableItems[0].InAnki);
        var sentenceSearch = await service.GetWorkspaceAsync(userId, "Polish", search: "meaning");
        Assert.Equal(sentenceId.ToString(), Assert.Single(sentenceSearch.AvailableItems).ItemId);
        var missing = await service.GetWorkspaceAsync(userId, "Polish", search: "missing");
        Assert.Empty(missing.AvailableItems);
        Assert.True(missing.HasCards);
        var selected = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Selected quiz",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        var unrelated = new Quiz { Id = Guid.NewGuid(), UserId = userId, Name = "Unrelated quiz",
            SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" };
        db.AddRange(selected, unrelated,
            new Word { Id = "selected-word", QuizId = selected.Id, Lemma = "new word", Translation = "new meaning", CreatedAt = created },
            new Word { Id = "unrelated-word", QuizId = unrelated.Id, Lemma = "not selected", Translation = "not in Anki", CreatedAt = created });
        await db.SaveChangesAsync();
        var preview = await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id);
        Assert.Equal(selected.Id, preview.SelectedQuizId);
        Assert.Equal(new[] { "selected-word", "word-104" }, preview.AvailableItems.Select(item => item.ItemId));
        Assert.False(preview.AvailableItems[0].InAnki);
        Assert.True(preview.AvailableItems[1].InAnki);
        Assert.Empty(await db.AnkiQuizLinks.ToListAsync()); // Previewing never adds the quiz.

        db.Words.Add(new Word { Id = "selected-duplicate", QuizId = selected.Id,
            Lemma = "target-104", Translation = "source-104", CreatedAt = created.AddSeconds(1) });
        await db.SaveChangesAsync();
        preview = await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id);
        Assert.Equal(2, preview.AvailableItems.Count);
        var duplicate = Assert.Single(preview.AvailableItems, item => item.TargetText == "target-104");
        Assert.True(duplicate.InAnki);
        Assert.Equal("word-104", duplicate.ItemId); // Removal must address the actual active note.
        await service.SetMembershipForLanguageAsync("Polish", "UTC", [],
            [new(duplicate.QuizId, duplicate.ItemType, duplicate.ItemId)], userId);
        preview = await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id);
        Assert.All(preview.AvailableItems, item => Assert.False(item.InAnki));
        Assert.Contains(preview.AvailableItems, item => item.ItemId == "selected-duplicate");

        await service.AddQuizForLanguageAsync("Polish", "UTC", selected.Id, false, userId);
        preview = await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id);
        Assert.All(preview.AvailableItems, item => Assert.True(item.InAnki));
        db.Words.Add(new Word { Id = "linked-duplicate", QuizId = selected.Id,
            Lemma = "target-104", Translation = "source-104", CreatedAt = created.AddSeconds(2) });
        await db.SaveChangesAsync();
        await service.SyncCollectionAsync(preview.Collection!.Id);
        var row = Assert.Single((await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id)).AvailableItems,
            item => item.TargetText == "target-104");
        Assert.True(row.InAnki);
        await service.SetMembershipAsync(preview.Collection.Id, [], [new(row.QuizId, row.ItemType, row.ItemId)], userId);
        db.ChangeTracker.Clear();
        service = new AnkiCollectionService(db, TimeProvider.System);
        await service.SyncCollectionAsync(preview.Collection.Id);
        var afterRemoval = await service.GetWorkspaceAsync(userId, "Polish", quizId: selected.Id);
        Assert.False(Assert.Single(afterRemoval.AvailableItems, item => item.TargetText == "target-104").InAnki);
        Assert.True(Assert.Single(afterRemoval.AvailableItems, item => item.ItemId == "selected-word").InAnki);

        var invalidSelection = await service.GetWorkspaceAsync(userId, "Polish", quizId: other.Id);
        Assert.NotEqual(other.Id, invalidSelection.SelectedQuizId);
        Assert.DoesNotContain(invalidSelection.AvailableItems, item => item.ItemId == "other-language");
        Assert.Empty((await service.GetWorkspaceAsync("someone-else", "Polish")).AvailableItems);
    }
}
