namespace Glosify.Services.Ai.Assistant;

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
    Guid? CreatedCollectionId = null,
    AssistantCreatedQuizSummary? CreatedQuiz = null)
{
    public Guid? AnkiCollectionId { get; init; }
    public int AnkiCardsRemoved { get; init; }
    public bool AnkiQuizUnlinked { get; init; }
    public int AnkiSelectedItems { get; init; }
    public int AnkiCardsAdded { get; init; }
    public int AnkiAlreadyIncluded { get; init; }
    public int AnkiExcludedCards { get; init; }

    /// <summary>Every row the apply created, changed, or removed, with enough state to undo it.</summary>
    public IReadOnlyList<AppliedChange> Journal { get; init; } = [];
}

/// <summary>
/// One change as it was actually saved. <paramref name="Before"/> is null for a creation and
/// <paramref name="After"/> is null for a deletion.
/// </summary>
public sealed record AppliedChange(
    string Kind,
    string EntityType,
    string EntityId,
    Guid? QuizId,
    object? Before,
    object? After);

public static class AppliedEntityTypes
{
    public const string AnkiCollection = "anki_collection";
    public const string AnkiCard = "anki_card";
    public const string AnkiQuizLink = "anki_quiz_link";
    public const string Word = "word";
    public const string Sentence = "sentence";
    public const string Quiz = "quiz";
    public const string Collection = "collection";
}

public sealed record AssistantCreatedQuizSummary(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage);
