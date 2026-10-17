using System.Text.Json;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnkiValidationTests
{
    private static readonly Guid CollectionId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SentenceId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid CardId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid OtherCardId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static ToolContext Context => new() { UserId = AssistantHarness.UserId, Mode = AssistantMode.Language,
        SourceLanguage = "English", TargetLanguage = "Polish", AnkiCollectionId = CollectionId };

    [Theory]
    [InlineData("remove_card", "missing")]
    [InlineData("remove_card", "empty")]
    [InlineData("remove_card", "unknown")]
    [InlineData("remove_card", "foreign")]
    [InlineData("add_quiz", "missing")]
    [InlineData("add_quiz", "false")]
    [InlineData("add_item", "missing")]
    [InlineData("add_item", "empty")]
    [InlineData("add_item", "unknown")]
    [InlineData("add_item", "wrong-type")]
    [InlineData("add_item", "unknown-type")]
    [InlineData("add_item", "invalid-sentence-id")]
    [InlineData("add_item", "foreign")]
    [InlineData("add_item", "wrong-quiz")]
    public async Task Invalid_action_arguments_are_rejected_before_creating_a_change(string action, string invalid)
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var args = new Dictionary<string, object?> { ["action"] = action, ["quiz_id"] = h.QuizId,
            ["direction"] = PracticeDirection.SourceToTarget };
        if (action == "remove_card" && invalid != "missing") args["card_id"] = invalid switch
            { "empty" => Guid.Empty, "foreign" => OtherCardId, _ => Guid.NewGuid() };
        if (action == "add_quiz" && invalid == "false")
        {
            args["words_source_to_target"] = false; args["words_target_to_source"] = false;
            args["sentences_source_to_target"] = false; args["sentences_target_to_source"] = false;
        }
        if (action == "add_item")
        {
            args["item_type"] = invalid switch { "invalid-sentence-id" => "sentences", "unknown-type" => "paragraphs", _ => "words" };
            if (invalid != "missing") args["item_id"] = invalid switch
                { "empty" => " ", "unknown" => "absent", "foreign" => "foreign-word", "wrong-type" => SentenceId.ToString(), _ => "word-id" };
            if (invalid == "foreign") args["quiz_id"] = await db.Quizzes.Where(q => q.UserId == "other").Select(q => q.Id).SingleAsync();
            if (invalid == "wrong-quiz") args["quiz_id"] = Guid.NewGuid();
        }
        var result = await AssistantToolFactory.Create(db).RunAsync("manage_anki", JsonSerializer.Serialize(args), Context);
        Assert.True(result.IsError);
        Assert.Empty(result.Changes);
        Assert.Empty(await db.AssistantChanges.ToListAsync());
        Assert.Equal(2, await db.AnkiCards.CountAsync());
    }

    [Theory]
    [InlineData(null, "word-id", "words")]
    [InlineData("word", "word-id", "words")]
    [InlineData("WORDS", "word-id", "words")]
    [InlineData("sentence", "22222222-2222-2222-2222-222222222222", "sentences")]
    [InlineData("sentences", "22222222-2222-2222-2222-222222222222", "sentences")]
    public async Task Valid_item_aliases_and_the_existing_default_produce_a_canonical_change(string? type, string id, string canonical)
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var result = await AssistantToolFactory.Create(db).RunAsync("manage_anki", JsonSerializer.Serialize(new
            { action = "add_item", quiz_id = h.QuizId, item_type = type, item_id = id, direction = PracticeDirection.SourceToTarget }), Context);
        Assert.False(result.IsError);
        var change = Assert.Single(result.Changes);
        Assert.Equal("anki_add_item", change.Kind);
        Assert.Equal(canonical, change.Payload.GetProperty("args").GetProperty("itemType").GetString());
    }

    [Fact]
    public async Task Valid_card_and_single_selected_quiz_direction_still_produce_changes()
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var tools = AssistantToolFactory.Create(db);
        var card = await tools.RunAsync("manage_anki", JsonSerializer.Serialize(new { action = "remove_card", card_id = CardId }), Context);
        Assert.False(card.IsError);
        Assert.Equal("anki_remove_card", Assert.Single(card.Changes).Kind);
        var quiz = await tools.RunAsync("manage_anki", JsonSerializer.Serialize(new
            { action = "add_quiz", quiz_id = h.QuizId, words_target_to_source = true }), Context);
        Assert.False(quiz.IsError);
        Assert.Equal("anki_add_quiz", Assert.Single(quiz.Changes).Kind);
    }

    private static async Task<AssistantHarness> SeedAsync()
    {
        var h = await AssistantHarness.CreateAsync();
        await using var db = h.Db();
        var otherQuiz = Guid.NewGuid();
        db.Quizzes.Add(new Quiz { Id = otherQuiz, UserId = "other", Name = "Other", Language = "Polish", SourceLanguage = "English", TargetLanguage = "Polish" });
        db.Words.AddRange(new Word { Id = "word-id", QuizId = h.QuizId, Lemma = "dom", Translation = "house" },
            new Word { Id = "foreign-word", QuizId = otherQuiz, Lemma = "kot", Translation = "cat" });
        db.QuizSentences.Add(new QuizSentence { Id = SentenceId, QuizId = h.QuizId, Text = "To jest dom.", Translation = "This is a house." });
        var otherCollection = Guid.NewGuid();
        db.AnkiCollections.AddRange(new AnkiCollection { Id = CollectionId, UserId = AssistantHarness.UserId, Name = "Study", TargetLanguage = "Polish" },
            new AnkiCollection { Id = otherCollection, UserId = "other", Name = "Other study", TargetLanguage = "Polish" });
        db.AnkiNotes.AddRange(new AnkiNote { Id = Guid.NewGuid(), AnkiCollectionId = CollectionId, QuizId = h.QuizId, WordId = "word-id", ItemType = "words", TargetText = "dom", SourceText = "house",
                Cards = [new AnkiCard { Id = CardId, Direction = PracticeDirection.SourceToTarget, IsActive = true, DirectlyIncluded = true }] },
            new AnkiNote { Id = Guid.NewGuid(), AnkiCollectionId = otherCollection, QuizId = otherQuiz, WordId = "foreign-word", ItemType = "words", TargetText = "kot", SourceText = "cat",
                Cards = [new AnkiCard { Id = OtherCardId, Direction = PracticeDirection.SourceToTarget, IsActive = true, DirectlyIncluded = true }] });
        await db.SaveChangesAsync();
        return h;
    }
}
