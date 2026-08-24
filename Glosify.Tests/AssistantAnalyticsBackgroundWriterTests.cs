using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnalyticsBackgroundWriterTests
{
    [Fact]
    public async Task Submitted_batch_is_persisted_by_the_background_worker()
    {
        var persisted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .AddInterceptors(new SignalAnalyticsSaveInterceptor(persisted))
            .Options;
        await using var seed = new FactoryBackedGlosifyContext(options);
        var factory = new TestDbContextFactory(seed);
        await using var services = new ServiceCollection()
            .AddScoped<IDbContextFactory<GlosifyContext>>(_ => factory)
            .BuildServiceProvider();
        var writer = new AssistantAnalyticsBackgroundWriter(
            services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<AssistantAnalyticsBackgroundWriter>.Instance);
        var invocation = new AssistantModelInvocation
        {
            Id = Guid.NewGuid(),
            TurnId = Guid.NewGuid(),
            Sequence = 0,
            Profile = "Librarian",
            Provider = "openai",
            RequestJson = "{}",
            Status = AssistantInvocationStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };

        await writer.StartAsync(CancellationToken.None);
        try
        {
            await writer.SubmitAsync([invocation], [], CancellationToken.None);
            await persisted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            await using var verification = factory.CreateDbContext();
            Assert.Equal(invocation.Id, (await verification.AssistantModelInvocations.SingleAsync()).Id);
        }
        finally
        {
            await writer.StopAsync(CancellationToken.None);
            writer.Dispose();
        }
    }

    private sealed class SignalAnalyticsSaveInterceptor(TaskCompletionSource persisted)
        : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(
            SaveChangesCompletedEventData eventData,
            int result,
            CancellationToken cancellationToken = default)
        {
            if (eventData.Context?.ChangeTracker.Entries<AssistantModelInvocation>().Any() == true)
            {
                persisted.TrySetResult();
            }

            return base.SavedChangesAsync(eventData, result, cancellationToken);
        }
    }
}
