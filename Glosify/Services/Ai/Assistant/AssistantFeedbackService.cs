using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

internal sealed class AssistantFeedbackService(GlosifyContext context, TimeProvider timeProvider)
{
    private static readonly HashSet<string> PositiveReasons =
    [
        "helpful", "correct", "clear", "saved_time", "tool_worked", "other",
    ];

    private static readonly HashSet<string> NegativeReasons =
    [
        "incorrect", "irrelevant", "confusing", "too_slow", "tool_failed",
        "unsafe_or_inappropriate", "other",
    ];

    public async Task<AssistantFeedbackView> UpsertAsync(
        Guid turnId,
        string userId,
        string rating,
        IReadOnlyCollection<string>? reasonCodes,
        string? comment,
        CancellationToken cancellationToken)
    {
        var normalizedRating = rating?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalizedRating is not AssistantFeedbackRating.Up and not AssistantFeedbackRating.Down)
        {
            throw new AssistantFeedbackValidationException("Rating must be either 'up' or 'down'.");
        }

        var allowedReasons = normalizedRating == AssistantFeedbackRating.Up
            ? PositiveReasons
            : NegativeReasons;
        var normalizedReasons = (reasonCodes ?? [])
            .Where(reason => !string.IsNullOrWhiteSpace(reason))
            .Select(reason => reason.Trim().ToLowerInvariant())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var invalidReason = normalizedReasons.FirstOrDefault(reason => !allowedReasons.Contains(reason));
        if (invalidReason is not null)
        {
            throw new AssistantFeedbackValidationException(
                $"Reason '{invalidReason}' is not valid for a {normalizedRating} rating.");
        }

        var normalizedComment = string.IsNullOrWhiteSpace(comment) ? null : comment.Trim();
        if (normalizedComment is { Length: > 1000 })
        {
            throw new AssistantFeedbackValidationException(
                "Feedback comments cannot exceed 1000 characters.");
        }

        var ownedTurn = await LoadOwnedTurnAsync(turnId, userId, cancellationToken);
        if (ownedTurn.Status != AssistantTurnStatus.Completed)
        {
            throw new AssistantFeedbackValidationException(
                "Feedback can only be saved for a completed assistant turn.");
        }

        var now = timeProvider.GetUtcNow();
        var feedback = await context.AssistantFeedback
            .Include(candidate => candidate.Reasons)
            .SingleOrDefaultAsync(candidate => candidate.TurnId == turnId, cancellationToken);
        if (feedback is null)
        {
            feedback = new AssistantFeedback
            {
                Id = Guid.NewGuid(),
                TurnId = turnId,
                Rating = normalizedRating,
                Comment = normalizedComment,
                CreatedAt = now,
                UpdatedAt = now,
            };
            context.AssistantFeedback.Add(feedback);
        }
        else
        {
            feedback.Rating = normalizedRating;
            feedback.Comment = normalizedComment;
            feedback.UpdatedAt = now;
        }

        var desiredReasons = normalizedReasons.ToHashSet(StringComparer.Ordinal);
        var removedReasons = feedback.Reasons
            .Where(reason => !desiredReasons.Contains(reason.ReasonCode))
            .ToList();
        context.AssistantFeedbackReasons.RemoveRange(removedReasons);
        foreach (var removedReason in removedReasons)
        {
            feedback.Reasons.Remove(removedReason);
        }
        foreach (var reason in normalizedReasons.Where(reason =>
            feedback.Reasons.All(existing => existing.ReasonCode != reason)))
        {
            feedback.Reasons.Add(new AssistantFeedbackReason
            {
                FeedbackId = feedback.Id,
                Feedback = feedback,
                ReasonCode = reason,
            });
        }
        await context.SaveChangesAsync(cancellationToken);

        AssistantAnalyticsTelemetry.RecordFeedback(
            turnId,
            ownedTurn.TraceId ?? string.Empty,
            normalizedRating,
            normalizedReasons);
        return Map(feedback);
    }

    public async Task DeleteAsync(Guid turnId, string userId, CancellationToken cancellationToken)
    {
        await LoadOwnedTurnAsync(turnId, userId, cancellationToken);
        var feedback = await context.AssistantFeedback
            .SingleOrDefaultAsync(candidate => candidate.TurnId == turnId, cancellationToken);
        if (feedback is null)
        {
            return;
        }

        context.AssistantFeedback.Remove(feedback);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RecordClientDurationAsync(
        Guid turnId,
        string userId,
        double clientDurationMs,
        CancellationToken cancellationToken)
    {
        if (!double.IsFinite(clientDurationMs) || clientDurationMs < 0 || clientDurationMs > 900_000)
        {
            throw new AssistantFeedbackValidationException(
                "Client duration must be between 0 and 900000 milliseconds.");
        }

        var turn = await LoadOwnedTurnAsync(turnId, userId, cancellationToken);
        turn.ClientDurationMs = clientDurationMs;
        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AssistantTurn> LoadOwnedTurnAsync(
        Guid turnId,
        string userId,
        CancellationToken cancellationToken)
    {
        var turn = await context.AssistantTurns
            .SingleOrDefaultAsync(candidate => candidate.Id == turnId, cancellationToken)
            ?? throw new AssistantTurnNotFoundException();
        var owned = await context.AssistantThreads
            .AnyAsync(thread => thread.Id == turn.ThreadId && thread.UserId == userId, cancellationToken);
        if (!owned)
        {
            throw new UnauthorizedAccessException("Assistant turn belongs to a different user.");
        }
        return turn;
    }

    internal static AssistantFeedbackView Map(AssistantFeedback feedback) => new(
        feedback.Rating,
        feedback.Reasons.Select(reason => reason.ReasonCode).Order().ToArray(),
        feedback.Comment,
        feedback.UpdatedAt);
}

internal sealed class AssistantFeedbackValidationException(string message) : Exception(message);

internal sealed class AssistantTurnNotFoundException() : Exception("Assistant turn not found.");
