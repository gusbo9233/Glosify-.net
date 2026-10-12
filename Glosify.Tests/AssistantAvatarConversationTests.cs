using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Auth;
using Glosify.Services.Avatar;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAvatarConversationTests
{
    [Fact]
    public async Task AcceptedRequestFreezesConversationAndRetryUsesSameSnapshot()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart();
        sessions.RecordTurn(session, "1", "user", "Chcę kawę.");
        sessions.RecordTurn(session, "1", "assistant", "Z mlekiem?");
        var input = new AssistantRunInput("Help me answer", AvatarConversationId: session.MemoryId);
        var run = await h.StartAsync("Help me answer", input: input, key: "snapshot");
        sessions.RecordTurn(session, "2", "user", "Later turn must not enter snapshot");
        var retried = await h.StartAsync("Help me answer", input: input, key: "snapshot");
        Assert.Equal(run.Id, retried.Id);
        await using (var db = h.Db())
        {
            var stored = await db.AssistantAvatarSnapshots.SingleAsync();
            Assert.Contains("Z mlekiem?", stored.ContentJson);
            Assert.DoesNotContain("Later turn", stored.ContentJson);
        }
        sessions.Forget(session.MemoryId, AssistantHarness.UserId);
        h.Restart();
        h.Model.ThenCall("read_avatar_conversation", new { message_id = (Guid?)null, offset = 0 }).ThenText("You can say: Tak, poproszę.");
        await h.DrainAsync();
        Assert.Contains(h.Model.Requests.SelectMany(r => r.History), t => t.ContentJson.Contains("Z mlekiem?", StringComparison.Ordinal));
        Assert.DoesNotContain(h.Model.Requests.SelectMany(r => r.History), t => t.ContentJson.Contains("Later turn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task OtherUsersAndLanguagesCannotAttachConversation()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart();
        foreach (var session in new[] { sessions.Create("other", QuizLanguageCatalog.Find("pl")!, "free"), sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("sv")!, "free") })
        {
            sessions.RecordTurn(session, "1", "user", "Private");
            var error = await Assert.ThrowsAsync<AvatarException>(() => h.StartAsync("Help", input: new("Help", AvatarConversationId: session.MemoryId)));
            Assert.Equal(404, error.Status);
        }
        await using var db = h.Db(); Assert.Empty(await db.AssistantRuns.ToListAsync());
    }
    [Fact]
    public async Task QuizCreationUsesConversationSnapshotAndExistingQuizWorkflow()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart();
        sessions.RecordTurn(session, "1", "user", "kawa");
        sessions.RecordTurn(session, "1", "assistant", "Kawa means coffee.");
        h.Model.ThenCall("read_avatar_conversation", new { message_id = (Guid?)null, offset = 0 })
            .ThenCall("create_quiz", new { name = "Our conversation", source_language = "English", target_language = "Polish", collection_id = (string?)null,
                words = new[] { new { word = "kawa", translation = "coffee" } }, sentences = (object?)null })
            .ThenText("Created a quiz from our conversation.");
        var run = await h.StartAsync("Make a quiz", input: new("Make a quiz", AvatarConversationId: session.MemoryId));
        await h.DrainAsync();
        await using var db = h.Db();
        var quiz = await db.Quizzes.SingleAsync(q => q.Name == "Our conversation");
        var word = await db.Words.SingleAsync(w => w.QuizId == quiz.Id);
        Assert.Equal("kawa", word.Lemma); Assert.Equal("coffee", word.Translation);
        Assert.Equal(AssistantHarness.UserId, quiz.UserId);
        Assert.Contains(h.Model.Requests.SelectMany(r => r.History), t => t.ContentJson.Contains("Kawa means coffee.", StringComparison.Ordinal));
        Assert.Equal(Glosify.Models.Entities.AssistantRunStatus.Completed, (await db.AssistantRuns.SingleAsync(r => r.Id == run.Id)).Status);
    }

    [Fact]
    public async Task LongConversationPagesRemainCompleteAndThreadScoped()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart();
        for (var i = 0; i < 45; i++) sessions.RecordTurn(session, i.ToString(), "user", $"Turn {i}: " + new string('x', 1100));
        await h.StartAsync("Read our conversation", input: new("Read our conversation", AvatarConversationId: session.MemoryId));
        await using var db = h.Db();
        var tool = new Glosify.Services.Ai.Assistant.Tools.ReadAvatarConversationTool(db);
        var context = new Glosify.Services.Ai.Assistant.Tools.ToolContext { UserId = AssistantHarness.UserId, ThreadId = h.ThreadId,
            Mode = Glosify.Services.Ai.Assistant.Tools.AssistantMode.Language, TargetLanguageCode = "pl" };
        var seen = new List<int>(); var offset = 0;
        do
        {
            var result = await tool.ExecuteAsync(System.Text.Json.JsonSerializer.Serialize(new { message_id = (Guid?)null, offset }), context, default);
            Assert.False(result.IsError);
            var data = System.Text.Json.JsonSerializer.SerializeToElement(result.Output, new System.Text.Json.JsonSerializerOptions(System.Text.Json.JsonSerializerDefaults.Web));
            var turns = data.GetProperty("turns").EnumerateArray().ToArray();
            Assert.InRange(turns.Sum(t => t.GetProperty("text").GetString()!.Length), 1, 12_000);
            seen.AddRange(turns.Select(t => t.GetProperty("sequence").GetInt32()));
            var nextOffset = data.GetProperty("next_offset").GetInt32(); Assert.True(nextOffset > offset); offset = nextOffset;
            if (!data.GetProperty("has_more").GetBoolean()) break;
        } while (offset < 45);
        Assert.Equal(Enumerable.Range(1, 45), seen);
        var denied = await tool.ExecuteAsync("{\"message_id\":null,\"offset\":0}", context with { ThreadId = Guid.NewGuid() }, default);
        Assert.True(denied.IsError);
    }

    [Theory]
    [InlineData("forgotten")]
    [InlineData("expired")]
    [InlineData("restart")]
    public async Task FollowUpAfterLiveContextEndsUsesRetainedSnapshot(string ending)
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart();
        sessions.RecordTurn(session, "1", "user", "Retained source text");
        var input = new AssistantRunInput("Help", AvatarConversationId: session.MemoryId);
        h.Model.ThenText("First answer");
        await h.StartAsync("Help", input: input); await h.DrainAsync();
        if (ending == "forgotten") sessions.Forget(session.MemoryId, AssistantHarness.UserId);
        else if (ending == "expired") h.Clock.Advance(TimeSpan.FromHours(9));
        else sessions = new AvatarSessions(h.Clock);
        h.Restart();
        h.Model.ThenCall("read_avatar_conversation", new { }).ThenText("Follow-up answer");
        var next = await h.StartAsync("Help again", input: input with { Message = "Help again" });
        await h.DrainAsync();
        await using var db = h.Db();
        Assert.Single(await db.AssistantAvatarSnapshots.ToListAsync());
        Assert.Equal(Glosify.Models.Entities.AssistantRunStatus.Completed, (await db.AssistantRuns.SingleAsync(r => r.Id == next.Id)).Status);
        Assert.Contains(h.Model.Requests.Last().History, t => t.ContentJson.Contains("Retained source text", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UnchangedRequestsReuseSourceAndNewTurnsCreateOneNewImmutableVersion()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var sessions = new AvatarSessions(h.Clock);
        var session = sessions.Create(AssistantHarness.UserId, QuizLanguageCatalog.Find("pl")!, "free");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Admin:UserIds:0"] = AssistantHarness.UserId }).Build();
        h.Configure = services => services.AddSingleton(new AvatarAssistantContext(sessions, new AdministratorAccess(config), Options.Create(new AvatarOptions { Enabled = true })));
        h.Restart(); sessions.RecordTurn(session, "1", "user", "Original");
        for (var i = 0; i < 3; i++)
        {
            h.Model.ThenText("Answer");
            await h.StartAsync($"Help {i}", input: new($"Help {i}", AvatarConversationId: session.MemoryId)); await h.DrainAsync();
        }
        await using (var db = h.Db())
        {
            var source = await db.AssistantAvatarSnapshots.SingleAsync();
            Assert.Equal(1, source.TurnCount);
            Assert.All(await db.AssistantRuns.ToListAsync(), run => Assert.Equal(source.Id, run.AvatarSnapshotId));
        }
        sessions.RecordTurn(session, "2", "assistant", "New spoken turn"); h.Clock.Advance(TimeSpan.FromSeconds(1));
        h.Model.ThenText("Updated");
        await h.StartAsync("New help", input: new("New help", AvatarConversationId: session.MemoryId)); await h.DrainAsync();
        await using (var db = h.Db())
        {
            var versions = await db.AssistantAvatarSnapshots.OrderBy(s => s.TurnCount).ToListAsync();
            Assert.Equal(2, versions.Count); Assert.DoesNotContain("New spoken turn", versions[0].ContentJson);
            Assert.Contains("New spoken turn", versions[1].ContentJson);
            sessions.Forget(session.MemoryId, AssistantHarness.UserId);
            var foreignThread = new Glosify.Models.Entities.AssistantThread { Id = Guid.NewGuid(), UserId = AssistantHarness.UserId, Language = "Polish", Title = "Other chat" };
            db.AssistantThreads.Add(foreignThread); await db.SaveChangesAsync();
            await Assert.ThrowsAsync<AvatarException>(() => h.StartAsync("Cross-chat", threadId: foreignThread.Id, input: new("Cross-chat", AvatarConversationId: session.MemoryId)));
            h.Language.CurrentLanguage = "Swedish";
            await Assert.ThrowsAsync<ArgumentException>(() => h.StartAsync("Other language", input: new("Other language", AvatarConversationId: session.MemoryId)));
            db.AssistantThreads.Remove(await db.AssistantThreads.SingleAsync(t => t.Id == h.ThreadId));
            await db.SaveChangesAsync(); Assert.Empty(await db.AssistantAvatarSnapshots.ToListAsync());
        }
    }

}
