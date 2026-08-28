namespace Glosify.Models.ViewModels;

/// <summary>
/// The assistant context owned by an individual Razor page. Keeping this as one typed
/// value avoids the layout having to interpret a collection of unrelated string keys.
/// </summary>
public sealed class AssistantPageContext
{
    public bool IsHidden { get; init; }
    public Guid? QuizId { get; init; }
    public string? FocusedWordId { get; init; }
    public Guid? DocumentId { get; init; }
    public int? CurrentPage { get; init; }
    public Guid? TranscriptId { get; init; }
    public int? TranscriptPage { get; init; }
    public string? TranscriptStream { get; init; }
    public string? Title { get; init; }
    public string? ContextLabel { get; init; }
    public string? EmptyText { get; init; }
    public string? Placeholder { get; init; }

    public static AssistantPageContext Hidden { get; } = new() { IsHidden = true };
}

/// <summary>
/// The resolved, display-ready settings rendered by the assistant panel.
/// </summary>
public class AssistantPanelViewModel
{
    public Guid? QuizId { get; set; }
    public string? FocusedWordId { get; set; }
    public Guid? DocumentId { get; set; }
    public int? CurrentPage { get; set; }
    public Guid? TranscriptId { get; set; }

    /// <summary>
    /// The transcript page on screen, set only by the transcript reader. The picker can
    /// point the chat at a different transcript, so the panel also carries
    /// <see cref="TranscriptId"/> to check the page still belongs to it.
    /// </summary>
    public int? TranscriptPage { get; set; }
    public string? TranscriptStream { get; set; }
    public string Title { get; set; } = "Assistant";
    public string ContextLabel { get; set; } = string.Empty;
    public string EmptyText { get; set; } = "Start a conversation.";
    public string Placeholder { get; set; } = "Ask the assistant...";
}

/// <summary>
/// Everything the assistant panel shell renders. Picker options are requested only when
/// the user opens the panel, so ordinary page requests do no assistant-library work.
/// </summary>
public sealed class AssistantPanelContentViewModel
{
    public required AssistantPanelViewModel Panel { get; init; }
    public required string ContextOptionsUrl { get; init; }
}

public sealed record AssistantContextOptions(
    IReadOnlyList<AssistantQuizOption> Quizzes,
    IReadOnlyList<AssistantMaterialOption> Books,
    IReadOnlyList<AssistantMaterialOption> Transcripts);

public sealed record AssistantQuizOption(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage);

public sealed record AssistantMaterialOption(Guid Id, string Title);
