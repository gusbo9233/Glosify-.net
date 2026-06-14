namespace Glosify.Services;

public interface IChangeApplier
{
    Task<AssistantApplyResult> ApplyAsync(
        Guid? quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken);
}

public sealed record AssistantApplyResult(
    int Applied,
    Guid? CreatedQuizId = null,
    Guid? CreatedCollectionId = null);
