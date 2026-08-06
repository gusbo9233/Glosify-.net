using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Mcp;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public class AssistantMcpToolSurfaceTests
{
    // A tool call from Foundry has no orchestrator turn to collect what it queued, so the
    // change has to reach the store or the user's request is silently dropped.
    [Fact]
    public async Task A_mutating_tool_call_persists_its_change_for_the_conversation()
    {
        await using var context = CreateContext();
        var session = await SeedAsync(context);
        var store = new AssistantPendingChangeStore(context);
        var surface = new AssistantMcpToolSurface(new AssistantTools(context), store);

        var result = await surface.ExecuteAsync(
            "add_text_input",
            """{"id":"row1","label":"1. ja bed{{blank}} w domu","expected_text":"e"}""",
            session,
            CancellationToken.None);

        var queued = JsonSerializer.SerializeToElement(result);
        Assert.True(queued.GetProperty("queued").GetBoolean());

        var stored = await store.ListPendingAsync(session.ConversationId, session.UserId);
        var change = Assert.Single(stored);
        Assert.Equal(PendingChangeKinds.AddCustomQuizElement, change.Kind);
        Assert.Equal("row1", change.Payload.GetProperty("block").GetProperty("id").GetString());
    }

    [Fact]
    public async Task Changes_from_separate_calls_accumulate_in_call_order()
    {
        await using var context = CreateContext();
        var session = await SeedAsync(context);
        var store = new AssistantPendingChangeStore(context);
        var surface = new AssistantMcpToolSurface(new AssistantTools(context), store);

        await surface.ExecuteAsync(
            "add_text_input",
            """{"id":"row1","label":"1. ja bed{{blank}}","expected_text":"e"}""",
            session, CancellationToken.None);
        await surface.ExecuteAsync(
            "add_submit_button", """{"id":"submit","text":"Check"}""",
            session, CancellationToken.None);
        await surface.ExecuteAsync(
            "add_feedback_message", """{"id":"feedback"}""",
            session, CancellationToken.None);

        var stored = await store.ListPendingAsync(session.ConversationId, session.UserId);

        Assert.Equal(3, stored.Count);
        Assert.Equal(
            new[] { "row1", "submit", "feedback" },
            stored.Select(c => c.Payload.GetProperty("block").GetProperty("id").GetString()!).ToArray());
    }

    // The duplicate-label guard reads what the turn already queued. Across MCP calls that
    // in-memory list is empty every time, so it has to be primed from the store.
    [Fact]
    public async Task Duplicate_answer_labels_are_still_caught_across_separate_calls()
    {
        await using var context = CreateContext();
        var session = await SeedAsync(context);
        var store = new AssistantPendingChangeStore(context);
        var surface = new AssistantMcpToolSurface(new AssistantTools(context), store);
        const string args = """{"id":"%ID%","label":"1. ja bed{{blank}} w domu","expected_text":"e"}""";

        await surface.ExecuteAsync("add_text_input", args.Replace("%ID%", "row1"), session, CancellationToken.None);
        var second = await surface.ExecuteAsync("add_text_input", args.Replace("%ID%", "row2"), session, CancellationToken.None);

        var element = JsonSerializer.SerializeToElement(second);
        Assert.True(element.TryGetProperty("error", out _));
        Assert.Single(await store.ListPendingAsync(session.ConversationId, session.UserId));
    }

    [Fact]
    public async Task Quiz_creation_tools_are_refused_over_mcp()
    {
        await using var context = CreateContext();
        var session = await SeedAsync(context);
        var surface = new AssistantMcpToolSurface(
            new AssistantTools(context),
            new AssistantPendingChangeStore(context));

        Assert.False(surface.Allows("create_custom_quiz"));
        Assert.False(surface.Allows("create_quiz"));

        var refused = JsonSerializer.SerializeToElement(await surface.ExecuteAsync(
            "create_custom_quiz", """{"name":"Second"}""", session, CancellationToken.None));

        Assert.Contains("not available", refused.GetProperty("error").GetString());
    }

    private static async Task<AssistantMcpSession> SeedAsync(GlosifyContext context)
    {
        var quizId = Guid.NewGuid();
        var customQuizId = Guid.NewGuid();
        context.Quizzes.Add(new Quiz
        {
            Id = quizId,
            UserId = "user-1",
            Name = "Polish",
            SourceLanguage = "English",
            TargetLanguage = "Polish",
            Language = "Polish",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        context.CustomQuizzes.Add(new CustomQuiz
        {
            Id = customQuizId,
            QuizId = quizId,
            Name = "Verb drills",
            DefinitionJson = """{"schemaVersion":1,"stylePreset":"editorial","blocks":[]}""",
            SchemaVersion = 1,
            IsPlayable = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        });
        await context.SaveChangesAsync();

        return new AssistantMcpSession
        {
            UserId = "user-1",
            QuizId = quizId,
            CustomQuizId = customQuizId,
            ConversationId = Guid.NewGuid(),
        };
    }

    private static GlosifyContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        return new GlosifyContext(options);
    }
}
