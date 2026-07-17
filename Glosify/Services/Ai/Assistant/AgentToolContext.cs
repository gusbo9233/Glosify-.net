namespace Glosify.Services.Ai.Assistant;

public sealed class AgentToolContext
{
    public Guid? QuizId { get; init; }
    public Guid? CustomQuizId { get; init; }
    public required string UserId { get; init; }
    public string? CurrentLanguage { get; init; }
    public string? FocusedWordId { get; init; }
    public string? FocusedWordLabel { get; init; }
    public string? PendingCustomQuizRef { get; set; }
    public string? PendingCustomQuizName { get; set; }
    public List<PendingChange> PendingChanges { get; } = [];
}
