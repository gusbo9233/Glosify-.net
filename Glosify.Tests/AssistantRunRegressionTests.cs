using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantRunRegressionTests
{
    private readonly string? _sqlConnection;

    public AssistantRunRegressionTests() { }

    private AssistantRunRegressionTests(string sqlConnection) => _sqlConnection = sqlConnection;

    private Task<AssistantHarness> CreateAsync(Action<GlosifyContext>? seed = null) =>
        _sqlConnection is null ? AssistantHarness.CreateAsync(seed) : AssistantHarness.CreateSqlServerAsync(_sqlConnection, seed);

    private static Task OnSqlServerAsync(Func<AssistantRunRegressionTests, Task> test) =>
        SqlServerTestDatabase.RunAsync("assistant_regression", db =>
            test(new AssistantRunRegressionTests(db.Database.GetConnectionString()!)));

    [SqlServerFact]
    public Task SqlServer_Resume() => OnSqlServerAsync(t => t.Resume_after_step_limit_should_call_model_again());

    [SqlServerFact]
    public Task SqlServer_Steering() => OnSqlServerAsync(t => t.Steering_before_write_checkpoint_should_prevent_old_write());

    [SqlServerFact]
    public Task SqlServer_ApprovalConflict() => OnSqlServerAsync(t => t.Approved_delete_should_keep_content_edited_since_proposal());

    [SqlServerFact]
    public Task SqlServer_ExplicitQuizConflict() => OnSqlServerAsync(t => t.Explicit_quiz_edits_should_also_detect_user_changes());

    [SqlServerFact]
    public Task SqlServer_SyncProposal() => OnSqlServerAsync(t => t.Sync_explicit_quiz_delete_should_remain_applicable());

    [Fact]
    public async Task Resume_after_step_limit_should_call_model_again()
    {
        await using var h = await CreateAsync();
        h.Options.MaxModelCalls = 2;
        h.Restart();
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = (string?)null, offset = (int?)null })
            .ThenText("More work remains.").ThenText("Finished remaining work.");
        var run = await h.RunAsync("Read everything");
        Assert.Equal(AssistantRunStatus.Paused, run.Status);
        await h.CommandAsync(run.Id, "resume");
        await h.DrainAsync();
        Assert.Equal(3, h.Model.Calls);
        var completed = await h.ViewAsync(run.Id);
        Assert.Equal(AssistantRunStatus.Completed, completed.Status);
        Assert.Equal("Finished remaining work.", completed.Parts.Last(p => p.Type == AssistantPartTypes.Text).Text);
    }

    [Fact]
    public async Task Steering_before_write_checkpoint_should_prevent_old_write()
    {
        await using var h = await CreateAsync();
        Guid runId = default;
        h.Configure = services =>
        {
            services.Remove(services.Single(d => d.ImplementationType == typeof(AddItemsTool)));
            services.AddScoped<IAssistantTool>(sp => new SteeringTool(new AddItemsTool(sp.GetRequiredService<GlosifyContext>()),
                () => h.CommandAsync(runId, "steer", "Do not add anything.")));
        };
        h.Restart();
        h.Model.ThenCall("add_items", new { quiz_id = (string?)null, words = new[] { new { word = "dom", translation = "house" } }, sentences = (object?)null })
            .ThenText("Okay.");
        var run = await h.StartAsync("Add dom");
        runId = run.Id;
        await h.DrainAsync();
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Empty(await db.AssistantChanges.ToListAsync());
        Assert.Equal(AssistantToolStates.Superseded, (await h.ViewAsync(run.Id)).Parts.Single(p => p.Type == AssistantPartTypes.Tool).State);
    }

    [Fact]
    public async Task Approved_delete_should_keep_content_edited_since_proposal()
    {
        await using var h = await CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null })
            .ThenText("Done.");
        var run = await h.RunAsync("Remove dom");
        Assert.Equal(AssistantRunStatus.AwaitingApproval, run.Status);
        await using (var db = h.Db())
        {
            (await db.Words.SingleAsync()).Translation = "user's new translation";
            await db.SaveChangesAsync();
        }
        await h.CommandAsync(run.Id, "approve");
        await h.DrainAsync();
        await using var after = h.Db();
        Assert.Equal("user's new translation", (await after.Words.SingleAsync()).Translation);
        Assert.Equal(AssistantToolStates.Error, (await h.ViewAsync(run.Id)).Parts.Single(p => p.Type == AssistantPartTypes.Tool).State);
    }

    [Fact]
    public async Task Explicit_quiz_edits_should_also_detect_user_changes()
    {
        await using var h = await CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = h.QuizId.ToString(), offset = (int?)null })
            .ThenCall("edit_items", new { quiz_id = h.QuizId.ToString(), words = new[] { new { id = "w1", word = (string?)null, translation = "home" } }, sentences = (object?)null })
            .ThenText("Done.");
        var run = await h.StartAsync("Edit dom", quizId: Guid.Empty);
        await h.StepAsync(); // initialize
        await h.StepAsync(); // list model call
        await h.StepAsync(); // list tool
        await h.StepAsync(); // transition to model
        await h.StepAsync(); // edit model call
        await using (var db = h.Db())
        {
            (await db.Words.SingleAsync()).Translation = "user's new translation";
            await db.SaveChangesAsync();
        }
        await h.DrainAsync();
        await using var after = h.Db();
        Assert.Equal("user's new translation", (await after.Words.SingleAsync()).Translation);
    }

    [Fact]
    public async Task Sync_explicit_quiz_delete_should_remain_applicable()
    {
        await using var h = await CreateAsync(db => db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" }));
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = h.QuizId.ToString(), offset = (int?)null })
            .ThenCall("delete_items", new { quiz_id = h.QuizId.ToString(), word_ids = new[] { "w1" }, sentence_ids = (string[]?)null })
            .ThenText("Please apply.");
        var run = await h.RunAsync("Remove dom", quizId: Guid.Empty, mode: AssistantRunModes.Sync);
        var result = await h.OrchestrateAsync(o => o.ApplyPendingChangesAsync(run.MessageId!.Value, AssistantHarness.UserId));
        Assert.Equal(1, result.Applied);
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Equal(0, (await h.OrchestrateAsync(o => o.ApplyPendingChangesAsync(run.MessageId!.Value, AssistantHarness.UserId))).Applied);
    }

    [SqlServerFact]
    public Task SqlServer_MultipleQuizProposal() => OnSqlServerAsync(t => t.Sync_proposal_preserves_separate_targets_and_applies_once());

    [Fact]
    public async Task Sync_proposal_preserves_separate_targets_and_applies_once()
    {
        var otherQuiz = Guid.NewGuid();
        await using var h = await CreateAsync(db =>
        {
            db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" });
            db.Quizzes.Add(new Quiz { Id = otherQuiz, UserId = AssistantHarness.UserId, Name = "Other Polish quiz", Language = "Polish", TargetLanguage = "Polish", SourceLanguage = "English" });
            db.Words.Add(new Word { Id = "w2", QuizId = otherQuiz, Lemma = "kot", Translation = "cat" });
        });
        h.Model.ThenCall("list_items", new { kind = "words", quiz_id = otherQuiz.ToString(), offset = (int?)null })
            .ThenCalls(null,
                ("delete_items", new { quiz_id = (string?)null, word_ids = new[] { "w1" }, sentence_ids = (string[]?)null }),
                ("delete_items", new { quiz_id = otherQuiz.ToString(), word_ids = new[] { "w2" }, sentence_ids = (string[]?)null }))
            .ThenText("Please apply both removals.");
        var run = await h.RunAsync("Remove both words", mode: AssistantRunModes.Sync);
        var result = await h.OrchestrateAsync(o => o.ApplyPendingChangesAsync(run.MessageId!.Value, AssistantHarness.UserId));
        Assert.Equal(2, result.Applied);
        Assert.Equal(new[] { h.QuizId, otherQuiz }, result.Journal.Select(c => c.QuizId!.Value));
        await using var db = h.Db();
        Assert.Empty(await db.Words.ToListAsync());
        Assert.Equal(0, (await h.OrchestrateAsync(o => o.ApplyPendingChangesAsync(run.MessageId!.Value, AssistantHarness.UserId))).Applied);
    }

    [Fact]
    public async Task Per_change_targets_cannot_bypass_ownership_or_partially_apply()
    {
        var otherQuiz = Guid.NewGuid();
        await using var h = await CreateAsync(db =>
        {
            db.Words.Add(new Word { Id = "w1", QuizId = Guid.Empty, Lemma = "dom", Translation = "house" });
            db.Quizzes.Add(new Quiz { Id = otherQuiz, UserId = "other", Name = "Private", Language = "Polish", TargetLanguage = "Polish", SourceLanguage = "English" });
            db.Words.Add(new Word { Id = "w2", QuizId = otherQuiz, Lemma = "kot", Translation = "cat" });
        });
        var changes = new[]
        {
            QuizContent.Change(PendingChangeKinds.DeleteWord, new { word_id = "w1", quiz_id = h.QuizId }),
            QuizContent.Change(PendingChangeKinds.DeleteWord, new { word_id = "w2", quiz_id = otherQuiz }),
        };
        await Assert.ThrowsAsync<Glosify.Services.Quizzes.QuizNotFoundException>(() => h.WithAsync(services =>
            services.GetRequiredService<IChangeApplier>().ApplyAsync(h.QuizId, AssistantHarness.UserId, changes, default)));
        await using var db = h.Db();
        Assert.Equal(2, await db.Words.CountAsync());
    }

    private sealed class SteeringTool(IAssistantTool inner, Func<Task<AssistantRunView>> steer) : IAssistantTool
    {
        public string Name => inner.Name;
        public AssistantToolKind Kind => inner.Kind;
        public bool Supports(AssistantMode mode) => inner.Supports(mode);
        public AgentToolDeclaration Declaration(AssistantMode mode) => inner.Declaration(mode);
        public async Task<ToolResult> ExecuteAsync(string args, ToolContext context, CancellationToken cancellationToken)
        {
            var result = await inner.ExecuteAsync(args, context, cancellationToken);
            await steer();
            return result;
        }
    }
}
