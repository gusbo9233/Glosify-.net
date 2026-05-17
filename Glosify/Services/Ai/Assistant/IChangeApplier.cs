namespace Glosify.Services;

public interface IChangeApplier
{
    Task<int> ApplyAsync(
        Guid quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken);
}
