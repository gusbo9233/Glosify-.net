using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantPracticeLinkTests
{
    private static readonly Guid SpanishId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ForeignId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static ToolContext Context => new() { UserId = AssistantHarness.UserId, Mode = AssistantMode.Language,
        TargetLanguage = "Spanish", TargetLanguageCode = "es" };

    [Theory]
    [InlineData("anki")]
    [InlineData("flashcards")]
    [InlineData("typing")]
    public async Task Practice_links_require_current_saved_language_even_when_selected_material_uses_another(string mode)
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var tools = AssistantToolFactory.Create(db);
        var wrongLanguage = await tools.RunAsync("open_practice", JsonSerializer.Serialize(new { id = SpanishId, mode }), Context);
        Assert.True(wrongLanguage.IsError);
        Assert.Equal("Different learning language", wrongLanguage.Title);
        Assert.False(JsonSerializer.SerializeToElement(wrongLanguage.Output).TryGetProperty("url", out _));

        var sameLanguage = await tools.RunAsync("open_practice", JsonSerializer.Serialize(new { id = h.QuizId, mode }), Context);
        Assert.False(sameLanguage.IsError);
        Assert.Equal(ExpectedUrl(mode, h.QuizId), JsonSerializer.SerializeToElement(sameLanguage.Output).GetProperty("url").GetString());

        // A queued request's context can be stale. A newly saved app selection wins.
        (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = "es";
        await db.SaveChangesAsync();
        var switched = await tools.RunAsync("open_practice", JsonSerializer.Serialize(new { id = SpanishId, mode }),
            Context with { TargetLanguage = "Polish", TargetLanguageCode = "pl" });
        Assert.False(switched.IsError);
        Assert.Equal(ExpectedUrl(mode, SpanishId), JsonSerializer.SerializeToElement(switched.Output).GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("anki")]
    [InlineData("flashcards")]
    [InlineData("typing")]
    public async Task Practice_links_still_reject_another_owners_resource(string mode)
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var result = await AssistantToolFactory.Create(db).RunAsync("open_practice", JsonSerializer.Serialize(new { id = ForeignId, mode }), Context);
        Assert.True(result.IsError);
        Assert.Equal("Not found", result.Title);
        Assert.False(JsonSerializer.SerializeToElement(result.Output).TryGetProperty("url", out _));
    }

    [Theory]
    [InlineData("anki", true)]
    [InlineData("flashcards", false)]
    [InlineData("typing", false)]
    public async Task No_selected_language_matches_existing_practice_route_behavior(string mode, bool rejected)
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        (await db.Users.SingleAsync(user => user.Id == AssistantHarness.UserId)).SelectedQuizLanguageCode = null;
        await db.SaveChangesAsync();
        var result = await AssistantToolFactory.Create(db).RunAsync("open_practice", JsonSerializer.Serialize(new { id = h.QuizId, mode }), Context);
        Assert.Equal(rejected, result.IsError);
        if (rejected) Assert.Equal("Choose a language", result.Title);
        else Assert.Equal(ExpectedUrl(mode, h.QuizId), JsonSerializer.SerializeToElement(result.Output).GetProperty("url").GetString());
    }

    private static string ExpectedUrl(string mode, Guid id) => mode == "anki" ? $"/Anki/Study?id={id}"
        : $"/Quiz/{(mode == "typing" ? "Type" : "Flashcard")}?id={id}&wordCount=20&practiceDirection=source-to-target&practiceItemType=words";

    private static async Task<AssistantHarness> SeedAsync()
    {
        var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        db.Quizzes.AddRange(new Quiz { Id = SpanishId, UserId = AssistantHarness.UserId, Name = "Spanish", SourceLanguage = "English", TargetLanguage = "Spanish", Language = "Spanish" },
            new Quiz { Id = ForeignId, UserId = "other", Name = "Other", SourceLanguage = "English", TargetLanguage = "Polish", Language = "Polish" });
        // The Anki ownership/language service accepts catalog aliases as well as names.
        db.AnkiCollections.AddRange(new AnkiCollection { Id = h.QuizId, UserId = AssistantHarness.UserId, Name = "Polish study", SourceLanguage = "English", TargetLanguage = "pl" },
            new AnkiCollection { Id = SpanishId, UserId = AssistantHarness.UserId, Name = "Spanish study", SourceLanguage = "English", TargetLanguage = "Spanish" },
            new AnkiCollection { Id = ForeignId, UserId = "other", Name = "Other study", SourceLanguage = "English", TargetLanguage = "Polish" });
        await db.SaveChangesAsync();
        return h;
    }
}
