namespace Glosify.Services;

public sealed class AgentToolContext
{
    public required Guid QuizId { get; init; }
    public required string UserId { get; init; }
    public required Quiz Quiz { get; init; }
    public string? FocusedWordId { get; init; }
    public string? FocusedWordLabel { get; init; }
    public List<PendingChange> PendingChanges { get; } = [];
}
