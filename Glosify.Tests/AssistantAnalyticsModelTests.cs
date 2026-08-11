using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Glosify.Tests;

public sealed class AssistantAnalyticsModelTests
{
    [Fact]
    public async Task Legacy_rows_remain_nullable_and_chat_delete_cascades_analytics()
    {
        await using var connection = new SqliteConnection("Data Source=:memory:");
        await connection.OpenAsync();
        var options = new DbContextOptionsBuilder<GlosifyContext>().UseSqlite(connection).Options;
        await using var context = new GlosifyContext(options);
        await context.Database.EnsureCreatedAsync();

        var user = new ApplicationUser
        {
            Id = "user-1",
            UserName = "user@example.test",
            NormalizedUserName = "USER@EXAMPLE.TEST",
            Email = "user@example.test",
            NormalizedEmail = "USER@EXAMPLE.TEST",
        };
        var thread = new AssistantThread
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Title = "Analytics",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        var turn = new AssistantTurn
        {
            Id = Guid.NewGuid(),
            ThreadId = thread.Id,
            Profile = "Librarian",
            Status = AssistantTurnStatus.Completed,
            StartedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow,
        };
        var invocation = new AssistantModelInvocation
        {
            Id = Guid.NewGuid(),
            TurnId = turn.Id,
            Sequence = 0,
            Profile = turn.Profile,
            Provider = "foundry",
            RequestJson = "{}",
            Status = AssistantInvocationStatus.Completed,
            StartedAt = turn.StartedAt,
            CompletedAt = turn.CompletedAt,
        };
        var feedback = new AssistantFeedback
        {
            Id = Guid.NewGuid(),
            TurnId = turn.Id,
            Rating = AssistantFeedbackRating.Up,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        feedback.Reasons.Add(new AssistantFeedbackReason
        {
            FeedbackId = feedback.Id,
            ReasonCode = "helpful",
        });
        context.AddRange(
            user,
            thread,
            turn,
            invocation,
            new AssistantToolExecution
            {
                Id = Guid.NewGuid(),
                TurnId = turn.Id,
                InvocationId = invocation.Id,
                Sequence = 0,
                ToolName = "lookup",
                ArgumentsJson = "{}",
                Status = AssistantInvocationStatus.Completed,
                StartedAt = turn.StartedAt,
                CompletedAt = turn.CompletedAt,
            },
            feedback,
            new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                TurnId = turn.Id,
                Sequence = 0,
                Role = AssistantMessageRole.User,
                ContentJson = "{}",
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new AssistantMessage
            {
                Id = Guid.NewGuid(),
                ThreadId = thread.Id,
                TurnId = null,
                Sequence = 1,
                Role = AssistantMessageRole.Model,
                ContentJson = "{}",
                Status = AssistantMessageStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
            },
            new AiCreditTransaction
            {
                UserId = user.Id,
                Kind = AiCreditTransactionKinds.UsageDebit,
                OperationId = null,
                AssistantTurnId = null,
            });
        await context.SaveChangesAsync();

        context.AssistantThreads.Remove(thread);
        await context.SaveChangesAsync();

        Assert.Empty(await context.AssistantTurns.ToListAsync());
        Assert.Empty(await context.AssistantModelInvocations.ToListAsync());
        Assert.Empty(await context.AssistantToolExecutions.ToListAsync());
        Assert.Empty(await context.AssistantFeedback.ToListAsync());
        Assert.Empty(await context.AssistantFeedbackReasons.ToListAsync());
        Assert.Empty(await context.AssistantMessages.ToListAsync());
        Assert.Single(await context.AiCreditTransactions.ToListAsync());
    }

    [Fact]
    public void Model_has_correlation_feedback_and_cost_indexes()
    {
        var options = new DbContextOptionsBuilder<GlosifyContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString("N"))
            .Options;
        using var context = new GlosifyContext(options);

        Assert.Contains(
            context.Model.FindEntityType(typeof(AssistantTurn))!.GetIndexes(),
            index => index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AssistantTurn.ThreadId), nameof(AssistantTurn.StartedAt)]));
        Assert.Contains(
            context.Model.FindEntityType(typeof(AssistantModelInvocation))!.GetIndexes(),
            index => index.IsUnique && index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(AssistantModelInvocation.TurnId), nameof(AssistantModelInvocation.Sequence)]));
        Assert.Contains(
            context.Model.FindEntityType(typeof(AssistantFeedback))!.GetIndexes(),
            index => index.IsUnique && index.Properties.Single().Name == nameof(AssistantFeedback.TurnId));
        Assert.Contains(
            context.Model.FindEntityType(typeof(AiCreditTransaction))!.GetIndexes(),
            index => index.Properties.Single().Name == nameof(AiCreditTransaction.OperationId));
        Assert.Contains(
            context.Model.FindEntityType(typeof(AiCreditTransaction))!.GetIndexes(),
            index => index.Properties.Single().Name == nameof(AiCreditTransaction.AssistantTurnId));
    }
}
