using System.Data.Common;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Abuse;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantCheckpointAccountingTests
{
    [Fact]
    public async Task Progress_is_prepared_before_accounting_and_is_charged_once()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await EnableAccountingAsync(h);
        var run = await h.StartAsync("Hello");
        var audit = new CheckpointQueries();
        h.Interceptors.Add(audit);

        await h.StepAsync();

        var accounting = audit.Commands.FindIndex(sql => sql.Contains("ResourceAccountingState", StringComparison.Ordinal));
        var preparation = audit.Commands.FindIndex(sql => sql.Contains("assistant_turns", StringComparison.Ordinal));
        Assert.True(preparation >= 0 && accounting > preparation,
            "Checkpoint preparation must precede the accounting lock/readiness query.\n" + string.Join("\n", audit.Commands));
        Assert.Single(audit.Commands, sql => sql.Contains("ResourceAccountingState", StringComparison.Ordinal));
        await using var db = h.Db();
        Assert.True(RunJson.Read<AssistantRunState>((await db.AssistantRuns.SingleAsync()).StateJson).Initialized);
        Assert.Contains(await db.Set<ResourceEntry>().ToListAsync(), entry => entry.EntityType == nameof(AssistantPart));
        await AssertExactUsageAsync(db);
        Assert.NotNull((await h.ViewAsync(run.Id)).MessageId);
    }

    [Fact]
    public async Task A_conflict_at_the_final_save_rolls_back_progress_and_accounting()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await EnableAccountingAsync(h);
        var run = await h.StartAsync("Hello");
        await using var before = h.Db();
        var entryIds = await before.Set<ResourceEntry>().AsNoTracking().Select(entry => entry.Id).ToListAsync();
        var usage = await before.Set<ResourceUsage>().AsNoTracking().ToDictionaryAsync(entry => (entry.Scope, entry.Resource), entry => entry.Used);
        var parts = await before.AssistantParts.CountAsync();
        var conflict = new ConflictAtSave();
        h.Interceptors.Add(conflict);

        await h.StepAsync();

        Assert.True(conflict.Injected);
        await using var after = h.Db();
        var saved = await after.AssistantRuns.SingleAsync();
        Assert.False(RunJson.Read<AssistantRunState>(saved.StateJson).Initialized);
        Assert.Null(saved.CurrentMessageId);
        Assert.Null(saved.LeaseId);
        Assert.Equal(parts, await after.AssistantParts.CountAsync());
        Assert.Equal(entryIds.Order(), (await after.Set<ResourceEntry>().Select(entry => entry.Id).ToListAsync()).Order());
        Assert.Equal(usage.OrderBy(pair => pair.Key), (await after.Set<ResourceUsage>().ToDictionaryAsync(entry => (entry.Scope, entry.Resource), entry => entry.Used)).OrderBy(pair => pair.Key));

        h.Model.ThenText("Hello!");
        await h.DrainAsync();
        Assert.Equal(AssistantRunStatus.Completed, (await h.ViewAsync(run.Id)).Status);
        await using var completed = h.Db();
        await AssertExactUsageAsync(completed);
    }

    [SqlServerFact]
    public Task Stop_can_commit_during_progress_preparation_and_discards_the_checkpoint() =>
        SqlServerTestDatabase.RunAsync("checkpoint_accounting", async db =>
        {
            await using var h = await AssistantHarness.CreateSqlServerAsync(db.Database.GetConnectionString()!);
            await EnableAccountingAsync(h);
            var run = await h.StartAsync("Hello");
            var gate = new PreparationGate();
            h.Interceptors.Add(gate);
            var step = h.StepAsync();
            try
            {
                await gate.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
                var stopped = await h.CommandAsync(run.Id, "cancel").WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(AssistantRunStatus.Cancelled, stopped.Status);
            }
            finally
            {
                gate.Continue.TrySetResult();
                await step.WaitAsync(TimeSpan.FromSeconds(15));
            }

            await using var after = h.Db();
            var saved = await after.AssistantRuns.SingleAsync();
            Assert.Equal(AssistantRunStatus.Cancelled, saved.Status);
            Assert.False(RunJson.Read<AssistantRunState>(saved.StateJson).Initialized);
            Assert.False(await after.AssistantParts.AnyAsync(part => part.Type == AssistantPartTypes.Context));
            await AssertExactUsageAsync(after);
        });

    private static async Task EnableAccountingAsync(AssistantHarness h)
    {
        await using var db = h.Db();
        db.Add(new ResourceAccountingState { Id = 1, Ready = true });
        await db.SaveChangesAsync();
        h.Configure = services => services.AddScoped(_ => h.Db(accountResources: true));
        h.Restart();
    }

    private static async Task AssertExactUsageAsync(GlosifyContext db)
    {
        var entries = await db.Set<ResourceEntry>().AsNoTracking().ToListAsync();
        var bytes = entries.Sum(entry => ResourceAccounting.Charges(entry.ChargesJson).GetValueOrDefault("content_bytes"));
        foreach (var scope in new[] { AssistantHarness.UserId, ResourceAccounting.Site })
            Assert.Equal(bytes, (await db.Set<ResourceUsage>().AsNoTracking().SingleAsync(entry => entry.Scope == scope && entry.Resource == "content_bytes")).Used);
    }

    private sealed class CheckpointQueries : DbCommandInterceptor
    {
        public List<string> Commands { get; } = [];

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add(command.CommandText);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ConflictAtSave : SaveChangesInterceptor
    {
        public bool Injected { get; private set; }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Injected)
            {
                // Make the real UPDATE affect zero rows, as when a command/reclaim wins the revision race.
                var run = eventData.Context!.ChangeTracker.Entries<AssistantRun>().Single();
                run.Property(candidate => candidate.Revision).OriginalValue = -1;
                Injected = true;
            }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class PreparationGate : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("assistant_turns", StringComparison.Ordinal) && Reached.TrySetResult())
                await Continue.Task.WaitAsync(cancellationToken);
            return result;
        }
    }
}
