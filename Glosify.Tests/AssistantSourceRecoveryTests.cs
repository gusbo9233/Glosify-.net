using System.Text.Json;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantSourceRecoveryTests
{
    [Theory]
    [InlineData(60, false)]
    [InlineData(300, true)]
    public async Task Follow_up_edit_recovers_original_text_after_summary(int lines, bool explicitMessage) =>
        await VerifyRecoveryAsync(lines, explicitMessage);

    [SqlServerFact]
    public Task SqlServer_follow_up_edit_recovers_original_text_after_summary() =>
        SqlServerTestDatabase.RunAsync("source_recovery", db => VerifyRecoveryAsync(60, false, db.Database.GetConnectionString()));

    private static async Task VerifyRecoveryAsync(int lines, bool explicitMessage, string? connection = null)
    {
        await using var h = connection is null ? await AssistantHarness.CreateAsync() : await AssistantHarness.CreateSqlServerAsync(connection);
        var sourceId = Guid.NewGuid();
        var summaryId = Guid.NewGuid();
        var source = string.Join("\n", Enumerable.Range(1, lines).Select(i => $"Wiersz {i}: pojechałem do domu po pracy."));
        await using (var db = h.Db())
        {
            db.Words.Add(new Word { Id = "w1", QuizId = h.QuizId, Lemma = "jechać", Translation = "to go" });
            db.AssistantMessages.AddRange(
                new AssistantMessage { Id = sourceId, ThreadId = h.ThreadId, Sequence = 1, Role = AssistantMessageRole.User,
                    ContentJson = RunJson.Text("user", source).ContentJson, CreatedAt = h.Clock.GetUtcNow() },
                new AssistantMessage { Id = summaryId, ThreadId = h.ThreadId, Sequence = 2, Role = AssistantMessageRole.Model,
                    ContentJson = RunJson.Text("model", "Created the quiz.").ContentJson, CreatedAt = h.Clock.GetUtcNow() });
            db.AssistantParts.Add(new AssistantPart { Id = Guid.NewGuid(), MessageId = summaryId,
                Type = AssistantPartTypes.Summary, Text = "Created a Polish quiz from the user's text.",
                MetadataJson = RunJson.Write(new { through_sequence = 2 }), CreatedAt = h.Clock.GetUtcNow().UtcDateTime });
            await db.SaveChangesAsync();
        }

        h.Model.ThenCall("read_source", new { from_line = lines, to_line = lines, message_id = explicitMessage ? sourceId : (Guid?)null })
            .ThenCall("edit_items", new { quiz_id = h.QuizId, words = new[] { new { id = "w1", word = "pojechałem", translation = "I went" } }, sentences = (object?)null })
            .ThenText("Updated to the exact form in the original text.");

        var run = await h.RunAsync("Use the word forms from the original text.");
        var history = AssistantHarness.Transcript(h.Model.Requests[0]);
        Assert.Contains(sourceId.ToString(), history);
        Assert.Contains("Saved source catalog", history);
        Assert.DoesNotContain($"Wiersz {lines}:", history);
        var response = h.Model.Requests[1].History.Select(turn => JsonDocument.Parse(turn.ContentJson).RootElement)
            .Where(root => root.TryGetProperty("parts", out _))
            .SelectMany(root => root.GetProperty("parts").EnumerateArray())
            .Last(part => part.TryGetProperty("name", out var name) && name.GetString() == "read_source"
                && part.TryGetProperty("responseJson", out _));
        var output = JsonDocument.Parse(response.GetProperty("responseJson").GetString()!).RootElement;
        Assert.Equal(sourceId, output.GetProperty("message_id").GetGuid());
        Assert.Contains($"Wiersz {lines}: pojechałem do domu po pracy.", output.GetProperty("lines").GetString());
        Assert.Equal(AssistantRunStatus.Completed, run.Status);
        Assert.Equal(1, run.SavedChanges);
        await using var after = h.Db();
        Assert.Equal("pojechałem", (await after.Words.SingleAsync()).Lemma);
        Assert.Empty(RunJson.Read<AssistantRunState>((await after.AssistantRuns.SingleAsync()).StateJson).SourceRead);
    }

    [Fact]
    public async Task Source_lookup_rejects_messages_outside_the_owned_chat()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var otherThread = Guid.NewGuid();
        var sourceId = Guid.NewGuid();
        await using var db = h.Db();
        db.AssistantThreads.Add(new AssistantThread { Id = otherThread, UserId = "other", Title = "Private", CreatedAt = h.Clock.GetUtcNow(), UpdatedAt = h.Clock.GetUtcNow() });
        db.AssistantMessages.Add(new AssistantMessage { Id = sourceId, ThreadId = otherThread, Role = "user", ContentJson = RunJson.Text("user", "private source").ContentJson, CreatedAt = h.Clock.GetUtcNow() });
        await db.SaveChangesAsync();
        var tool = new ReadSourceTool(db, new AssistantMessagePresenter());
        foreach (var threadId in new[] { h.ThreadId, otherThread })
        {
            var result = await tool.ExecuteAsync(RunJson.Write(new { from_line = 1, message_id = sourceId }),
                new ToolContext { UserId = AssistantHarness.UserId, Mode = AssistantMode.Language, ThreadId = threadId }, default);
            Assert.True(result.IsError);
            Assert.DoesNotContain("private source", RunJson.Write(result.Output));
        }
    }
}
