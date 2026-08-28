namespace Glosify.Services.Ai.Assistant;

public sealed class AgentToolContext
{
    public Guid? QuizId { get; init; }
    public required string UserId { get; init; }
    public string? CurrentLanguage { get; init; }
    public string? CurrentLanguageCode { get; init; }
    public bool IsFreestyle { get; init; }

    /// <summary>The language quiz content is translated into, when it is known.</summary>
    /// <remarks>
    /// Lets a creation tool fill in a source language the application already knows instead of
    /// making the model ask for one it was told at the start of the conversation.
    /// </remarks>
    public string? SourceLanguage { get; init; }

    public string? FocusedWordId { get; init; }
    public string? FocusedWordLabel { get; init; }
    public Guid? TranscriptId { get; init; }
    public Guid? BookDocumentId { get; init; }
    /// <summary>
    /// The content type the request asked for, when it said so.
    /// </summary>
    /// <remarks>
    /// Narrowing the offered tools is not enough on its own: a published agent can declare a
    /// tool the application did not offer, and a resumed chat carries calls from an older
    /// surface. The handlers check this too, so an explicit sentence request cannot be stored
    /// as vocabulary no matter which path produced the call.
    /// </remarks>
    public AssistantContentKind RequestedContentKind { get; init; } = AssistantContentKind.Auto;
    public List<PendingChange> PendingChanges { get; } = [];
}
