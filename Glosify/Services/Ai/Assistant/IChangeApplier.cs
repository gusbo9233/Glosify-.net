namespace Glosify.Services.Ai.Assistant;

public interface IChangeApplier
{
    Task<AssistantApplyResult> ApplyAsync(
        Guid? quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken);
}

/// <param name="CreatedCustomQuizElements">
/// How many elements landed in the created custom quiz. Zero means the agent queued the
/// shell but never filled it, so the client must not invite the user into an empty editor.
/// </param>
public sealed record AssistantApplyResult(
    int Applied,
    Guid? CreatedQuizId = null,
    Guid? CreatedCollectionId = null,
    Guid? CreatedCustomQuizId = null,
    int CreatedCustomQuizElements = 0,
    AssistantCreatedQuizSummary? CreatedQuiz = null);

public sealed record AssistantCreatedQuizSummary(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage);
