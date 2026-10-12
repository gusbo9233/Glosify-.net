using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Auth;
using Glosify.Services.RealtimeTranslation;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantEvaluationRecoveryTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_evaluations_are_reclaimed_after_expiry_or_legacy_missing_lease(bool legacy)
    {
        await using var h = await SeedAsync();
        await using (var db = h.Db())
        {
            var row = await db.AssistantToolEvaluations.SingleAsync();
            row.Status = "evaluating";
            row.LeaseId = legacy ? null : Guid.NewGuid();
            row.LeaseUntil = legacy ? null : h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        var credits = new Credits();
        await using (var db = h.Db()) await Worker(h, db, new Evaluator(_ => Task.FromResult(Result("recovered"))), credits).EvaluateOneAsync(default);
        await using var after = h.Db();
        var evaluation = await after.AssistantToolEvaluations.SingleAsync();
        Assert.Equal("evaluated", evaluation.Status);
        Assert.Contains("recovered", evaluation.EvaluationJson!);
        Assert.Null(evaluation.LeaseId);
        Assert.Null(evaluation.LeaseUntil);
        Assert.Single(credits.Committed);
    }

    [Fact]
    public async Task Live_claim_is_not_evaluated_by_another_worker()
    {
        await using var h = await SeedAsync();
        await using var db = h.Db();
        var row = await db.AssistantToolEvaluations.SingleAsync();
        row.Status = "evaluating"; row.LeaseId = Guid.NewGuid(); row.LeaseUntil = h.Clock.GetUtcNow().UtcDateTime.AddMinutes(2);
        await db.SaveChangesAsync();
        var credits = new Credits();
        await Worker(h, db, new Evaluator(_ => throw new InvalidOperationException("Should not evaluate")), credits).EvaluateOneAsync(default);
        Assert.Empty(credits.Reserved);
        Assert.Equal("evaluating", (await db.AssistantToolEvaluations.AsNoTracking().SingleAsync()).Status);
    }

    [Fact]
    public async Task Late_worker_cannot_overwrite_reclaimed_evaluation_or_charge_it()
    {
        await using var h = await SeedAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayed = new TaskCompletionSource<ToolUseEvaluation>(TaskCreationOptions.RunContinuationsAsynchronously);
        var credits = new Credits();
        await using var firstDb = h.Db();
        var first = Worker(h, firstDb, new Evaluator(_ => { entered.SetResult(); return delayed.Task; }), credits).EvaluateOneAsync(default);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        h.Clock.Advance(AssistantEvaluationWorker.LeaseDuration + TimeSpan.FromSeconds(1));
        await using (var secondDb = h.Db()) await Worker(h, secondDb, new Evaluator(_ => Task.FromResult(Result("new"))), credits).EvaluateOneAsync(default);
        delayed.SetResult(Result("old"));
        await first;
        await using var after = h.Db();
        var row = await after.AssistantToolEvaluations.SingleAsync();
        Assert.Contains("new", row.EvaluationJson!);
        Assert.DoesNotContain("old", row.EvaluationJson!);
        Assert.Equal(2, credits.Reserved.Count);
        Assert.Single(credits.Committed);
        Assert.Single(credits.Released);
        Assert.NotEqual(credits.Committed[0], credits.Released[0]);
    }

    [Fact]
    public async Task Shutdown_returns_owned_evaluation_to_pending_and_releases_reservation()
    {
        await using var h = await SeedAsync();
        using var stopping = new CancellationTokenSource();
        var credits = new Credits();
        await using (var db = h.Db())
        {
            var worker = Worker(h, db, new Evaluator(token => { stopping.Cancel(); return Task.FromCanceled<ToolUseEvaluation>(token); }), credits);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => worker.EvaluateOneAsync(stopping.Token));
        }
        await using var after = h.Db();
        var row = await after.AssistantToolEvaluations.SingleAsync();
        Assert.Equal("pending", row.Status);
        Assert.Null(row.LeaseId);
        Assert.Single(credits.Released);
        Assert.Empty(credits.Committed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Evaluation_and_actual_credit_debit_commit_together_across_restart(bool interruptSettlement)
    {
        await using var h = await SeedAsync();
        using var stopping = new CancellationTokenSource();
        await using (var db = h.Db())
        {
            // SQLite cannot generate SQL Server rowversions; seed one explicitly.
            var storedDate = db.Model.FindEntityType(typeof(AiCreditAccount))!.FindProperty(nameof(AiCreditAccount.CreatedAt))!
                .GetTypeMapping().Converter!.ConvertToProvider(h.Clock.GetUtcNow());
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AiCreditAccounts (UserId, BalanceCredits, ReservedCredits, CreatedAt, UpdatedAt, RowVersion) VALUES ('user', 100, 0, {storedDate}, {storedDate}, X'00')");
            var credits = RealCredits(h, db);
            IAiCreditService billing = interruptSettlement ? new InterruptedSettlement(credits, stopping) : credits;
            var task = Worker(h, db, new Evaluator(_ => Task.FromResult(Result("first") with { Tokens = 1000 })), billing).EvaluateOneAsync(stopping.Token);
            if (interruptSettlement) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            else await task;
        }
        await using (var db = h.Db())
        {
            var row = await db.AssistantToolEvaluations.SingleAsync();
            var account = await db.AiCreditAccounts.SingleAsync();
            Assert.Equal(interruptSettlement ? "pending" : "evaluated", row.Status);
            Assert.Equal(interruptSettlement ? 100m : 99m, account.BalanceCredits);
            Assert.Equal(0m, account.ReservedCredits);
            Assert.Equal(interruptSettlement ? 0 : 1, await db.AiCreditTransactions.CountAsync(t => t.Kind == AiCreditTransactionKinds.UsageDebit));
            await Worker(h, db, new Evaluator(_ => Task.FromResult(Result("retry") with { Tokens = 1000 })), RealCredits(h, db)).EvaluateOneAsync(default);
        }
        await using var after = h.Db();
        var completed = await after.AssistantToolEvaluations.SingleAsync();
        Assert.Equal("evaluated", completed.Status);
        var debit = await after.AiCreditTransactions.SingleAsync(t => t.Kind == AiCreditTransactionKinds.UsageDebit);
        Assert.Equal(completed.Id, debit.OperationId);
        Assert.Equal(-1m, debit.CreditAmount);
        Assert.Equal(99m, (await after.AiCreditAccounts.SingleAsync()).BalanceCredits);
        Assert.Equal(0m, (await after.AiCreditAccounts.SingleAsync()).ReservedCredits);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reclaim_releases_crashed_reservations_and_monthly_budget_before_retry(bool canRetry)
    {
        await using var h = await SeedAsync();
        var settings = new AiUsageOptions
        {
            MonthlyBudget = new()
            {
                Enabled = true, TimeZoneId = "UTC", Providers = ["typesafe"],
                Models = [new() { Deployment = new JevOptions().Model, InputSekPerMillionTokens = 100, OutputSekPerMillionTokens = 100 }],
            },
        };
        Guid abandoned;
        Guid unrelated;
        await using (var db = h.Db())
        {
            var date = db.Model.FindEntityType(typeof(AiCreditAccount))!.FindProperty(nameof(AiCreditAccount.CreatedAt))!
                .GetTypeMapping().Converter!.ConvertToProvider(h.Clock.GetUtcNow());
            var period = h.Clock.GetUtcNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AiCreditAccounts (UserId, BalanceCredits, ReservedCredits, CreatedAt, UpdatedAt, RowVersion) VALUES ('user', 10, 0, {date}, {date}, X'00')");
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO AiMonthlyBudgets (PeriodKey, LimitMicros, SpentMicros, ReservedMicros, OverrunMicros, CreatedAt, UpdatedAt, RowVersion) VALUES ({period}, 300000000, 0, 0, 0, {date}, {date}, X'00')");
            var row = await db.AssistantToolEvaluations.SingleAsync();
            row.Status = "evaluating";
            row.LeaseId = Guid.NewGuid();
            row.LeaseUntil = h.Clock.GetUtcNow().UtcDateTime.AddMinutes(-1);
            await db.SaveChangesAsync();
            var credits = RealCredits(h, db, settings);
            abandoned = (await credits.ReserveAsync(new(AssistantHarness.UserId, AiUsageFeatures.Assistant,
                "tool_use_evaluation", row.Id), "typesafe", new JevOptions().Model, 9000)).ReservationId;
            unrelated = (await credits.ReserveAsync(new(AssistantHarness.UserId, AiUsageFeatures.Assistant,
                "tool_use_evaluation", Guid.NewGuid()), "typesafe", new JevOptions().Model, 500)).ReservationId;
            Assert.Equal(9.5m, (await db.AiCreditAccounts.SingleAsync()).ReservedCredits);
            // Dispose without releasing, as a process crash after ReserveAsync would do.
        }

        await using (var db = h.Db())
        {
            var workerSettings = canRetry ? settings : new AiUsageOptions(); // no typesafe price configured
            var worker = new AssistantEvaluationWorker(db, new Evaluator(_ => Task.FromResult(Result("recovered") with { Tokens = 1000 })),
                Options.Create(new JevOptions { Enabled = true }), RealCredits(h, db, settings), Options.Create(workerSettings), h.Clock);
            await worker.EvaluateOneAsync(default);
        }

        await using var after = h.Db();
        Assert.Equal(canRetry ? "evaluated" : "unavailable", (await after.AssistantToolEvaluations.SingleAsync()).Status);
        var release = await after.AiCreditTransactions.SingleAsync(t => t.ReservationId == abandoned && t.Kind == AiCreditTransactionKinds.Release);
        Assert.Equal(9m, release.CreditAmount);
        Assert.True(release.BudgetAmountMicros > 0);
        Assert.False(await after.AiCreditTransactions.AnyAsync(t => t.ReservationId == unrelated && t.Kind == AiCreditTransactionKinds.Release));
        var account = await after.AiCreditAccounts.SingleAsync();
        Assert.Equal(0.5m, account.ReservedCredits);
        Assert.Equal(canRetry ? 9m : 10m, account.BalanceCredits);
        var budget = await after.AiMonthlyBudgets.SingleAsync();
        Assert.Equal((await after.AiCreditTransactions.SingleAsync(t => t.ReservationId == unrelated)).BudgetAmountMicros, budget.ReservedMicros);
        Assert.Equal(canRetry, budget.SpentMicros > 0);
    }

    private static AiCreditService RealCredits(AssistantHarness h, GlosifyContext db, AiUsageOptions? settings = null)
    {
        var usage = Options.Create(settings ?? new AiUsageOptions { MonthlyBudget = new() { Enabled = false } });
        var pricing = new CreditPricingResolver(Options.Create(new CreditPricingOptions { DefaultModelMultiplier = 1m }), usage, Options.Create(new RealtimeTranslationOptions()));
        return new(db, new ContextFactory(h), usage, pricing, new TrialEligibilityService(db), h.Clock);
    }

    private sealed class ContextFactory(AssistantHarness h) : IDbContextFactory<GlosifyContext>
    {
        public GlosifyContext CreateDbContext() => h.Db();
    }

    private sealed class InterruptedSettlement(AiCreditService credits, CancellationTokenSource stopping) : IAiCreditService
    {
        public Task<AiCreditReservation> ReserveAsync(AiUsageContext context, string provider, string model, int estimatedTokens, CancellationToken cancellationToken = default) => credits.ReserveAsync(context, provider, model, estimatedTokens, cancellationToken);
        public async Task CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken = default)
        {
            await credits.CommitUsageAsync(reservationId, usage, cancellationToken);
            // Simulate shutdown after the credit service saved, before settlement commits.
            stopping.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
        }
        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) => credits.ReleaseAsync(reservationId, cancellationToken);
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) => credits.GetOrCreateAccountAsync(userId, cancellationToken);
        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(string userId, int count = 25, CancellationToken cancellationToken = default) => credits.GetRecentTransactionsAsync(userId, count, cancellationToken);
        public Task GrantAsync(string adminUserId, string targetUserId, int creditsToGrant, string note, CancellationToken cancellationToken = default) => credits.GrantAsync(adminUserId, targetUserId, creditsToGrant, note, cancellationToken);
    }

    private static async Task<AssistantHarness> SeedAsync()
    {
        var h = await AssistantHarness.CreateAsync();
        h.Model.ThenText("Done.");
        var run = await h.RunAsync("Hello");
        await using var db = h.Db();
        db.AssistantToolEvaluations.Add(new AssistantToolEvaluation { Id = Guid.NewGuid(), RunId = run.Id, PartId = Guid.NewGuid(), ToolName = "list_library",
            CreatedAt = h.Clock.GetUtcNow().UtcDateTime, SnapshotHash = "hash", SnapshotJson = RunJson.Write(new ToolDecisionSnapshot("Hello", [], new { quizId = h.QuizId }, new[] { new { name = "list_library" } }, "list_library", JsonSerializer.SerializeToElement(new { }), [])) });
        await db.SaveChangesAsync();
        return h;
    }
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Invalid_evaluated_usage_releases_credits_and_records_unavailable(int tokens)
    {
        await using var h = await SeedAsync();
        var credits = new Credits();
        await using (var db = h.Db()) await Worker(h, db, new Evaluator(_ => Task.FromResult(Result("invalid") with { Tokens = tokens })), credits).EvaluateOneAsync(default);
        await using var after = h.Db();
        Assert.Equal("unavailable", (await after.AssistantToolEvaluations.SingleAsync()).Status);
        Assert.Empty(credits.Committed);
        Assert.Equal(credits.Reserved.Single(), credits.Released.Single());
    }

    private static ToolUseEvaluation Result(string name) => new("evaluated", name, "test", [], 100);
    private static AssistantEvaluationWorker Worker(AssistantHarness h, GlosifyContext db, IToolUseEvaluator evaluator, IAiCreditService credits) =>
        new(db, evaluator, Options.Create(new JevOptions { Enabled = true }), credits, Options.Create(new AiUsageOptions { MonthlyBudget = new() { Enabled = false } }), h.Clock);
    private sealed class Evaluator(Func<CancellationToken, Task<ToolUseEvaluation>> run) : IToolUseEvaluator
    {
        public Task<ToolUseEvaluation> EvaluateAsync(ToolDecisionSnapshot snapshot, CancellationToken cancellationToken) => run(cancellationToken);
    }
    private sealed class Credits : IAiCreditService
    {
        public List<Guid> Reserved { get; } = [];
        public List<Guid> Committed { get; } = [];
        public List<Guid> Released { get; } = [];
        public Task<AiCreditReservation> ReserveAsync(AiUsageContext context, string provider, string model, int estimatedTokens, CancellationToken cancellationToken = default)
        {
            var id = Guid.NewGuid(); Reserved.Add(id);
            return Task.FromResult(new AiCreditReservation(id, context.UserId, 1, estimatedTokens));
        }
        public Task CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken = default) { Committed.Add(reservationId); return Task.CompletedTask; }
        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) { Released.Add(reservationId); return Task.CompletedTask; }
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(string userId, int count = 25, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task GrantAsync(string adminUserId, string targetUserId, int credits, string note, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
