using System.Data.Common;
using System.Threading.Channels;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Assistant;
using Glosify.Services.Ai.Assistant.Runtime;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantWorkerPollingTests
{
    [Fact]
    public async Task Idle_worker_backs_off_recovers_periodically_and_wakes_without_waiting_for_timeout()
    {
        await using var h = await AssistantHarness.CreateAsync();
        var queries = new QueryCounter();
        h.Interceptors.Add(queries);
        h.Configure = services => services.AddScoped(sp => new AssistantEvaluationWorker(
            sp.GetRequiredService<GlosifyContext>(), sp.GetRequiredService<IToolUseEvaluator>(),
            Options.Create(new JevOptions { Enabled = false }), new UnexpectedCredits(),
            Options.Create(new AiUsageOptions()), h.Clock));
        h.Restart();
        await using var scope = h.Scope();
        var signals = scope.ServiceProvider.GetRequiredService<AssistantRunSignals>();
        var clock = new ObservedClock(h.Clock);
        using var worker = new AssistantRunWorker(scope.ServiceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new AssistantRuntimeOptions { Workers = 1 }), NullLogger<AssistantRunWorker>.Instance, signals, clock);
        await worker.StartAsync(default);
        try
        {
            var polls = 0;
            foreach (var seconds in new[] { 1, 2, 4, 8, 16, 30, 30 })
            {
                var delay = await clock.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10));
                Assert.Equal(TimeSpan.FromSeconds(seconds), delay);
                // Both run claims and disabled-evaluation settlement recovery are still checked.
                Assert.Equal(++polls * 2, queries.Reads);
                h.Clock.Advance(delay);
            }

            Assert.Equal(TimeSpan.FromSeconds(30), await clock.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            var beforeWake = queries.Reads;
            signals.Notify(Guid.NewGuid());
            // No clock advance: notification must interrupt the full 30-second wait.
            Assert.Equal(TimeSpan.FromSeconds(1), await clock.Delays.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            Assert.Equal(beforeWake + 2, queries.Reads);
        }
        finally
        {
            await worker.StopAsync(default).WaitAsync(TimeSpan.FromSeconds(10));
        }
        Assert.True(worker.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Starting_a_run_wakes_all_workers_even_before_they_begin_waiting()
    {
        await using var h = await AssistantHarness.CreateAsync();
        await using var scope = h.Scope();
        var signals = scope.ServiceProvider.GetRequiredService<AssistantRunSignals>();
        var firstWorker = signals.WorkAvailable;
        var secondWorker = signals.WorkAvailable;
        var run = await h.StartAsync("Hello");
        await Task.WhenAll(firstWorker, secondWorker).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal("queued", run.Status);
        Assert.False(signals.WorkAvailable.IsCompleted);
    }

    private sealed class QueryCounter : DbCommandInterceptor
    {
        internal int Reads;
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Reads);
            return ValueTask.FromResult(result);
        }
    }

    private sealed class ObservedClock(TimeProvider inner) : TimeProvider
    {
        internal Channel<TimeSpan> Delays { get; } = Channel.CreateUnbounded<TimeSpan>();
        public override DateTimeOffset GetUtcNow() => inner.GetUtcNow();
        public override long GetTimestamp() => inner.GetTimestamp();
        public override long TimestampFrequency => inner.TimestampFrequency;
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = inner.CreateTimer(callback, state, dueTime, period);
            Delays.Writer.TryWrite(dueTime);
            return timer;
        }
    }

    private sealed class UnexpectedCredits : IAiCreditService
    {
        public Task<AiCreditAccountView> GetOrCreateAccountAsync(string userId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idle workers must not create credit accounts.");
        public Task<IReadOnlyList<AiCreditTransaction>> GetRecentTransactionsAsync(string userId, int count = 25,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Idle workers must not read credit history.");
        public Task GrantAsync(string adminUserId, string targetUserId, int credits, string note,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Idle workers must not grant credits.");
        public Task<AiCreditReservation> ReserveAsync(AiUsageContext context, string provider, string model, int estimatedTokens,
            CancellationToken cancellationToken = default) => throw new InvalidOperationException("Idle workers must not reserve credits.");
        public Task CommitUsageAsync(Guid reservationId, AiTokenUsage usage, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idle workers must not charge credits.");
        public Task ReleaseAsync(Guid reservationId, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Idle workers must not release credits.");
    }
}
