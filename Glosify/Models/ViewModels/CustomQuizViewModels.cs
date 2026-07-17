using Glosify.Models.CustomQuizzes;
using Glosify.Services.CustomQuizzes;

namespace Glosify.Models.ViewModels;

public sealed class CustomQuizEditorViewModel
{
    public Quiz Quiz { get; set; } = null!;
    public IReadOnlyList<Word> Words { get; set; } = [];
    public CustomQuizEditorDto Editor { get; set; } = null!;
    public IReadOnlyList<CustomQuizTemplateDto> Templates { get; set; } = [];
}

public sealed class CustomQuizPlayViewModel
{
    public CustomQuizPlayData Play { get; set; } = null!;
}
