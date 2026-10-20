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
    public async Task Picker_pages_and_searches_all_items_without_losing_collection_metadata()
    {
        await using var db = new GlosifyContext(new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
        await VerifyAsync(db);
    }

    [SqlServerFact]
    public Task SqlServer_picker_pages_and_searches_all_items() =>
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
        Assert.Equal(100, first.AvailableItems.Count);
        Assert.True(first.HasMoreItems);
        Assert.True(first.HasCards); // The selected card is not on this picker page.
        Assert.Equal(new[] { "English" }, first.SourceLanguages);
        Assert.All(first.AvailableItems, item => Assert.False(item.InAnki));
        var second = await service.GetWorkspaceAsync(userId, "Polish", itemOffset: 100);
        Assert.Equal(6, second.AvailableItems.Count);
        Assert.False(second.HasMoreItems);
        Assert.Empty(first.AvailableItems.Select(item => item.ItemId).Intersect(second.AvailableItems.Select(item => item.ItemId)));
        Assert.True(second.AvailableItems.Single(item => item.ItemId == "word-104").InAnki);
        Assert.Equal(sentenceId.ToString(), second.AvailableItems.Last().ItemId);
        var sentencePage = await service.GetWorkspaceAsync(userId, "Polish", itemOffset: 105);
        Assert.Equal(sentenceId.ToString(), Assert.Single(sentencePage.AvailableItems).ItemId);
        var found = await service.GetWorkspaceAsync(userId, "Polish", search: "target-104");
        Assert.Equal("word-104", Assert.Single(found.AvailableItems).ItemId);
        Assert.True(found.AvailableItems[0].InAnki);
        var sentenceSearch = await service.GetWorkspaceAsync(userId, "Polish", search: "meaning");
        Assert.Equal(sentenceId.ToString(), Assert.Single(sentenceSearch.AvailableItems).ItemId);
        var missing = await service.GetWorkspaceAsync(userId, "Polish", search: "missing");
        Assert.Empty(missing.AvailableItems);
        Assert.True(missing.HasCards);
        Assert.Empty((await service.GetWorkspaceAsync("someone-else", "Polish")).AvailableItems);
    }
}
