using Glosify.Models.CustomQuizzes;
using Glosify.Services.CustomQuizzes;

namespace Glosify.Models.ViewModels;

public sealed class CustomQuizEditorViewModel
{
    public QuizCard Quiz { get; set; } = null!;
    public IReadOnlyList<WordRow> Words { get; set; } = [];
    public CustomQuizEditorDto Editor { get; set; } = null!;
    public IReadOnlyList<CustomQuizTemplateDto> Templates { get; set; } = [];
}

public sealed class CustomQuizPlayViewModel
{
    public CustomQuizPlayData Play { get; set; } = null!;
}
