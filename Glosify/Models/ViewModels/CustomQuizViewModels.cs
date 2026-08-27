using System.Text.RegularExpressions;
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
    public IReadOnlyList<CustomQuizPlayBlockViewModel> Blocks { get; set; } = [];

    public static CustomQuizPlayViewModel Create(CustomQuizPlayData play, string defaultAnswerLabel) => new()
    {
        Play = play,
        Blocks = play.Document.Blocks
            .OrderBy(block => block.Order)
            .Select(block => CustomQuizPlayBlockViewModel.Create(block, play.ResolvedValues, defaultAnswerLabel))
            .ToList(),
    };
}

public sealed class CustomQuizPlayBlockViewModel
{
    private static readonly Regex InlineBlankPattern = new(
        @"\{\{blank\}\}|\{blank\}|_{2,}|\.{3,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public CustomQuizBlockV1 Block { get; private init; } = null!;
    public string ResolvedValue { get; private init; } = string.Empty;
    public CustomQuizInlineAnswerViewModel InlineAnswer { get; private init; } = new(string.Empty, string.Empty);
    public IReadOnlyList<CustomQuizPlayOptionViewModel> Options { get; private init; } = [];

    public static CustomQuizPlayBlockViewModel Create(
        CustomQuizBlockV1 block,
        IReadOnlyDictionary<string, string> resolvedValues,
        string defaultAnswerLabel)
    {
        var label = string.IsNullOrWhiteSpace(block.Label)
            ? defaultAnswerLabel
            : block.Label.Trim();
        var blank = InlineBlankPattern.Match(label);
        var inlineAnswer = blank.Success
            ? new CustomQuizInlineAnswerViewModel(
                label[..blank.Index].TrimEnd(),
                label[(blank.Index + blank.Length)..].TrimStart())
            : new CustomQuizInlineAnswerViewModel(label, string.Empty);

        return new CustomQuizPlayBlockViewModel
        {
            Block = block,
            ResolvedValue = resolvedValues.GetValueOrDefault($"block:{block.Id}", string.Empty),
            InlineAnswer = inlineAnswer,
            Options = block.Options
                .Select(option => new CustomQuizPlayOptionViewModel(
                    option,
                    resolvedValues.GetValueOrDefault($"option:{block.Id}:{option.Id}", string.Empty)))
                .ToList(),
        };
    }
}

public sealed record CustomQuizInlineAnswerViewModel(string Before, string After);

public sealed record CustomQuizPlayOptionViewModel(
    CustomQuizOptionV1 Option,
    string ResolvedValue);
