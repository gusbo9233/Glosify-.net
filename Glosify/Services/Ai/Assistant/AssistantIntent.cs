namespace Glosify.Services.Ai.Assistant;

/// <summary>Which kind of content a request is about.</summary>
public enum AssistantContentKind
{
    /// <summary>The request does not say, so both content families stay available.</summary>
    Auto,
    Words,
    Sentences,
    Both,
}

/// <summary>Which kind of quiz a request is about.</summary>
public enum AssistantArtifactKind
{
    /// <summary>The request does not say, so the page context decides as before.</summary>
    Auto,
    StandardQuiz,
}

/// <summary>Whether a request asks for a new artifact or for additions to an existing one.</summary>
/// <remarks>
/// Recorded, never enforced. Unlike artifact and content this does not narrow the tool
/// surface: creation and addition already separate cleanly by which tools the page offers,
/// so acting on a misread here would remove a capability for no gain. It exists because a
/// router target has to state the operation, and the request is the only place it is stated
/// before tools run.
/// </remarks>
public enum AssistantOperationKind
{
    /// <summary>The request does not say.</summary>
    Auto,
    Create,
    Add,
}

/// <summary>
/// What the application has decided a turn is asking for, before the model is invoked.
/// </summary>
/// <remarks>
/// Glosify is strong once a tool call exists — proposals are reviewed, handlers check
/// ownership, and Apply is transactional — but artifact and content type used to be inferred
/// freely by the model, which is where quizzes became custom quizzes and sentences became
/// words. Anything explicit in the request belongs here instead, so the same decision can
/// narrow the offered tools and be enforced again at the execution boundary.
/// </remarks>
public sealed record AssistantIntent(
    AssistantArtifactKind ArtifactKind,
    AssistantContentKind ContentKind,
    AssistantOperationKind OperationKind = AssistantOperationKind.Auto)
{
    public static readonly AssistantIntent Unknown =
        new(AssistantArtifactKind.Auto, AssistantContentKind.Auto, AssistantOperationKind.Auto);
}
