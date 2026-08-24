namespace Glosify.Models.ViewModels;

/// <summary>
/// What the page being rendered knows about the assistant's context. Supplied by the
/// layout from ViewData; everything else the panel shows is loaded by
/// <see cref="Glosify.ViewComponents.AssistantPanelViewComponent"/>.
/// </summary>
public class AssistantPanelViewModel
{
    public Guid? QuizId { get; set; }
    public string? FocusedWordId { get; set; }
    public Guid? DocumentId { get; set; }
    public int? CurrentPage { get; set; }
    public Guid? CustomQuizId { get; set; }
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
/// Everything the assistant panel renders: the page's context plus the picker options
/// loaded for it. The options are projections, not entities, so the view cannot reach
/// past what it is meant to show and trigger further loading.
/// </summary>
public sealed class AssistantPanelContentViewModel
{
    public required AssistantPanelViewModel Panel { get; init; }
    public IReadOnlyList<AssistantQuizOption> Quizzes { get; init; } = [];
    public IReadOnlyList<AssistantMaterialOption> Books { get; init; } = [];
    public IReadOnlyList<AssistantMaterialOption> Transcripts { get; init; } = [];

    /// <summary>
    /// The material picker carries books and transcripts in one control, so the option
    /// value encodes which kind it is. Matches the parsing in wwwroot/js/assistant.js.
    /// </summary>
    public string SelectedMaterial =>
        Panel.TranscriptId.HasValue ? $"transcript:{Panel.TranscriptId}"
        : Panel.DocumentId.HasValue ? $"book:{Panel.DocumentId}"
        : string.Empty;
}

public sealed record AssistantQuizOption(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage);

public sealed record AssistantMaterialOption(Guid Id, string Title);
