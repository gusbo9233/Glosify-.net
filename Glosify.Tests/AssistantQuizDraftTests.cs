using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Ai.Assistant;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class AssistantQuizDraftTests
{
    [Fact]
    public async Task Batches_build_one_quiz_beyond_the_per_call_limit_in_source_order()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = NewContext();
        string? draftId = null;
        for (var batch = 0; batch < 3; batch++)
        {
            var result = await Call(tools, context, new
            {
                name = "Whole source",
                draft_id = draftId,
                complete = batch == 2,
                words = Enumerable.Range(batch * 80, 80).Select(i => new { word = $"word{i}", translation = $"meaning{i}" }),
                sentences = Enumerable.Range(batch * 50, 50).Select(i => new { text = $"Sentence {i}.", translation = $"Translation {i}." }),
            });
            draftId = result.GetProperty("draft_id").GetString();
            Assert.Equal((batch + 1) * 80, result.GetProperty("word_count").GetInt32());
            Assert.Equal((batch + 1) * 50, result.GetProperty("sentence_count").GetInt32());
            Assert.Equal(batch == 2, result.GetProperty("complete").GetBoolean());
        }

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal(Enumerable.Range(0, 240).Select(i => $"word{i}"),
            payload.GetProperty("words").EnumerateArray().Select(word => word.GetProperty("word").GetString()));
        Assert.Equal("Sentence 149.", payload.GetProperty("sentences")[149].GetProperty("text").GetString());
        Assert.Empty(db.Quizzes); // All batches remain a proposal until Apply.
    }

    [Fact]
    public async Task Repeated_batches_deduplicate_and_sentences_win_across_batches()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = NewContext();
        var first = await Call(tools, context, new
        {
            name = "Source",
            words = new[] { new { word = "dom", translation = "house" }, new { word = "To jest dom", translation = "This is a house" } },
        });
        var batch = new
        {
            draft_id = first.GetProperty("draft_id").GetString(),
            words = new[] { new { word = " DOM ", translation = "home" }, new { word = "nowy", translation = "new" } },
            sentences = new[] { new { text = "To jest dom.", translation = "This is a house." } },
            complete = true,
        };
        await Call(tools, context, batch);
        await Call(tools, context, batch);

        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal(new[] { "dom", "nowy" }, payload.GetProperty("words").EnumerateArray().Select(word => word.GetProperty("word").GetString()));
        Assert.Equal("house", payload.GetProperty("words")[0].GetProperty("translation").GetString());
        Assert.Single(payload.GetProperty("sentences").EnumerateArray());
    }

    [Fact]
    public async Task Unknown_draft_cannot_target_another_turn_or_create_a_quiz()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var firstContext = NewContext();
        var first = await Call(tools, firstContext, new { name = "Private draft" });
        var otherContext = NewContext();
        var result = await Call(tools, otherContext, new { draft_id = first.GetProperty("draft_id").GetString(), complete = true });

        Assert.True(result.TryGetProperty("error", out _));
        Assert.Empty(otherContext.PendingChanges);
        Assert.False(Assert.Single(firstContext.PendingChanges).Payload.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Overflow_is_recoverable_in_next_batch_and_prevents_false_completion()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = NewContext();
        var first = await Call(tools, context, new
        {
            name = "Source", complete = true,
            words = Enumerable.Range(0, 101).Select(i => new { word = $"word{i}", translation = $"meaning{i}" }),
        });
        Assert.False(first.GetProperty("complete").GetBoolean());
        Assert.Equal(100, first.GetProperty("word_count").GetInt32());
        Assert.Equal(100, Assert.Single(first.GetProperty("skipped_words").EnumerateArray()).GetProperty("Index").GetInt32());

        var result = await Call(tools, context, new
        {
            draft_id = first.GetProperty("draft_id").GetString(), complete = true,
            words = new[] { new { word = "word100", translation = "meaning100" } },
        });
        Assert.True(result.GetProperty("complete").GetBoolean());
        Assert.Equal(101, Assert.Single(context.PendingChanges).Payload.GetProperty("words").GetArrayLength());
    }

    [Fact]
    public async Task Continuation_preserves_destination_and_content_kind_guards()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", SourceLanguage = "English", CurrentLanguage = "Polish", RequestedContentKind = AssistantContentKind.Words };
        var first = await Call(tools, context, new { name = "Original" });
        var draftId = first.GetProperty("draft_id").GetString();
        var rejected = await Call(tools, context, new
        {
            draft_id = draftId, complete = true,
            sentences = new[] { new { text = "To jest dom.", translation = "This is a house." } },
        });
        Assert.True(rejected.TryGetProperty("error", out _));
        Assert.False(Assert.Single(context.PendingChanges).Payload.GetProperty("complete").GetBoolean());

        await Call(tools, context, new { draft_id = draftId, name = "Changed", target_language = "French", collection_id = Guid.NewGuid(), complete = true });
        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal("Original", payload.GetProperty("name").GetString());
        Assert.Equal("Polish", payload.GetProperty("target_language").GetString());
        Assert.Equal(JsonValueKind.Null, payload.GetProperty("collection_id").ValueKind);
        Assert.True(payload.GetProperty("complete").GetBoolean());
    }

    [Fact]
    public async Task Freestyle_batches_use_the_same_draft_protocol()
    {
        await using var db = CreateContext();
        var tools = AssistantToolFactory.Create(db);
        var context = new AgentToolContext { UserId = "user-1", IsFreestyle = true };
        var first = await Call(tools, context, new { name = "Study", items = new[] { new { prompt = "One?", answer = "First" } } });
        await Call(tools, context, new { draft_id = first.GetProperty("draft_id").GetString(), complete = true, items = new[] { new { prompt = "Two?", answer = "Second" } } });
        var payload = Assert.Single(context.PendingChanges).Payload;
        Assert.Equal(2, payload.GetProperty("words").GetArrayLength());
        Assert.True(payload.GetProperty("complete").GetBoolean());
        var schema = JsonSerializer.SerializeToElement(Assert.Single(tools.FreestyleLibrarianDeclarations, tool => tool.Name == "create_quiz").ParametersJsonSchema);
        Assert.True(schema.GetProperty("properties").TryGetProperty("draft_id", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("complete", out _));
    }

    private static AgentToolContext NewContext() => new() { UserId = "user-1", SourceLanguage = "English", CurrentLanguage = "Polish" };
    private static GlosifyContext CreateContext() => new(new DbContextOptionsBuilder<GlosifyContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options);
    private static async Task<JsonElement> Call(IAssistantTools tools, AgentToolContext context, object args) =>
        JsonSerializer.SerializeToElement(await tools.ExecuteAsync(context.IsFreestyle ? "create_quiz" : "create_vocabulary_quiz", JsonSerializer.Serialize(args), context, CancellationToken.None));
}
