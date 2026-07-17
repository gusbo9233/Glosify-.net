using System.Text.Json.Serialization;

namespace Glosify.Models.CustomQuizzes;

public static class CustomQuizBlockTypes
{
    public const string Heading = "quiz_heading";
    public const string InstructionLabel = "instruction_label";
    public const string PromptLabel = "prompt_label";
    public const string TranslationLabel = "translation_label";
    public const string TextInput = "text_input";
    public const string Textarea = "textarea";
    public const string Checkbox = "checkbox";
    public const string RadioGroup = "radio_group";
    public const string MultiSelectGroup = "multi_select_group";
    public const string SelectMenu = "select_menu";
    public const string WordBank = "word_bank";
    public const string SubmitButton = "submit_button";
    public const string FeedbackMessage = "feedback_message";

    public static readonly HashSet<string> All =
    [
        Heading, InstructionLabel, PromptLabel, TranslationLabel, TextInput, Textarea,
        Checkbox, RadioGroup, MultiSelectGroup, SelectMenu, WordBank, SubmitButton,
        FeedbackMessage
    ];

    public static bool IsAnswer(string type) => type is TextInput or Textarea or Checkbox
        or RadioGroup or MultiSelectGroup or SelectMenu;

    public static bool IsChoice(string type) => type is RadioGroup or MultiSelectGroup or SelectMenu;
}

public static class CustomQuizWordFields
{
    public const string Lemma = "lemma";
    public const string Translation = "translation";

    public static bool IsValid(string? field) => field is Lemma or Translation;
}

public static class CustomQuizStylePresets
{
    public const string Editorial = "editorial";
    public const string Aurora = "aurora";
    public const string Paper = "paper";
    public const string WordLab = "word_lab";

    public static readonly HashSet<string> All = [Editorial, Aurora, Paper, WordLab];
}

public sealed class CustomQuizDocumentV1
{
    public int SchemaVersion { get; set; } = 1;
    public string StylePreset { get; set; } = CustomQuizStylePresets.Editorial;
    public List<CustomQuizBlockV1> Blocks { get; set; } = [];
}

public sealed class CustomQuizBlockV1
{
    public string Id { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public int Order { get; set; }
    public int ColumnSpan { get; set; } = 12;
    // Grid coordinates are optional for documents saved before visual positioning
    // was introduced. The service assigns collision-free coordinates when they are 0.
    public int GridColumn { get; set; }
    public int GridRow { get; set; }
    public string? Text { get; set; }
    public string? Label { get; set; }
    public CustomQuizWordBindingV1? Binding { get; set; }
    public CustomQuizWordBindingV1? ExpectedBinding { get; set; }
    public string? ExpectedText { get; set; }
    public bool ExpectedChecked { get; set; }
    public List<CustomQuizOptionV1> Options { get; set; } = [];
    public List<string> TargetInputIds { get; set; } = [];
}

public sealed class CustomQuizWordBindingV1
{
    public string WordId { get; set; } = string.Empty;
    public string Field { get; set; } = CustomQuizWordFields.Lemma;
}

public sealed class CustomQuizOptionV1
{
    public string Id { get; set; } = string.Empty;
    public CustomQuizWordBindingV1 Binding { get; set; } = new();
    public bool IsCorrect { get; set; }
}

public sealed record CustomQuizSummaryDto(
    Guid Id,
    Guid QuizId,
    string Name,
    bool IsPlayable,
    DateTimeOffset UpdatedAt);

public sealed record CustomQuizEditorDto(
    Guid? Id,
    Guid QuizId,
    string Name,
    CustomQuizDocumentV1 Document,
    bool IsPlayable,
    IReadOnlyList<string> PlayabilityErrors,
    string RowVersion);

public sealed class SaveCustomQuizRequest
{
    public Guid QuizId { get; set; }
    public string Name { get; set; } = string.Empty;
    public CustomQuizDocumentV1 Document { get; set; } = new();
    public string? RowVersion { get; set; }
}

public sealed class CustomQuizAnswerInput
{
    public string BlockId { get; set; } = string.Empty;
    public List<string> Values { get; set; } = [];
}

public sealed class GradeCustomQuizRequest
{
    public Guid AttemptId { get; set; }
    public Guid? ClassroomId { get; set; }
    public List<CustomQuizAnswerInput> Answers { get; set; } = [];
}

public sealed record CustomQuizBlockGrade(
    string BlockId,
    string State,
    string Message,
    IReadOnlyList<string> CorrectValues);

public sealed record CustomQuizGradeResult(
    string State,
    int CorrectCount,
    int TotalCount,
    int ScorePercent,
    IReadOnlyList<CustomQuizBlockGrade> Blocks);

public sealed record CustomQuizValidationResult(
    bool IsStructurallyValid,
    bool IsPlayable,
    IReadOnlyList<string> StructuralErrors,
    IReadOnlyList<string> PlayabilityErrors);

public sealed record CustomQuizTemplateSummary(
    string Id,
    string Name,
    string Description,
    string StylePreset,
    string Icon,
    string BestFor,
    string LayoutGuidance);

public sealed record CustomQuizTemplateDto(
    string Id,
    string Name,
    string Description,
    string StylePreset,
    string Icon,
    string BestFor,
    string LayoutGuidance,
    CustomQuizDocumentV1 Document);

public sealed class CustomQuizValidationException : Exception
{
    public CustomQuizValidationException(IReadOnlyList<string> errors)
        : base("The custom quiz document is invalid.") => Errors = errors;

    public IReadOnlyList<string> Errors { get; }
}

public sealed class CustomQuizConcurrencyException : Exception;
