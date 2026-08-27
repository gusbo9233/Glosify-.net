using Glosify.Models.CustomQuizzes;
using Glosify.Models.ViewModels;
using Glosify.Services.CustomQuizzes;
using Xunit;

namespace Glosify.Tests;

public sealed class CustomQuizPlayViewModelTests
{
    [Theory]
    [InlineData("Translate {{blank}} now", "Translate", "now")]
    [InlineData("Before {BLANK} after", "Before", "after")]
    [InlineData("Word ____ suffix", "Word", "suffix")]
    [InlineData("Prefix... ending", "Prefix", "ending")]
    [InlineData("No placeholder", "No placeholder", "")]
    [InlineData("   ", "Localized answer", "")]
    public void Create_builds_inline_answer_parts(
        string label,
        string expectedBefore,
        string expectedAfter)
    {
        var block = new CustomQuizBlockV1
        {
            Id = "answer",
            Type = CustomQuizBlockTypes.TextInput,
            Label = label,
        };

        var model = CustomQuizPlayViewModel.Create(PlayData(block), "Localized answer");

        var answer = Assert.Single(model.Blocks).InlineAnswer;
        Assert.Equal(expectedBefore, answer.Before);
        Assert.Equal(expectedAfter, answer.After);
    }

    [Fact]
    public void Create_orders_blocks_and_resolves_block_and_option_display_values()
    {
        var later = new CustomQuizBlockV1
        {
            Id = "choices",
            Type = CustomQuizBlockTypes.RadioGroup,
            Order = 2,
            Options = [new() { Id = "first" }],
        };
        var earlier = new CustomQuizBlockV1
        {
            Id = "prompt",
            Type = CustomQuizBlockTypes.PromptLabel,
            Order = 1,
        };
        var resolved = new Dictionary<string, string>
        {
            ["block:prompt"] = "Resolved prompt",
            ["option:choices:first"] = "Resolved option",
        };

        var model = CustomQuizPlayViewModel.Create(PlayData(later, earlier, resolved), "Answer");

        Assert.Collection(
            model.Blocks,
            block =>
            {
                Assert.Equal("prompt", block.Block.Id);
                Assert.Equal("Resolved prompt", block.ResolvedValue);
            },
            block =>
            {
                Assert.Equal("choices", block.Block.Id);
                Assert.Equal("Resolved option", Assert.Single(block.Options).ResolvedValue);
            });
    }

    private static CustomQuizPlayData PlayData(params CustomQuizBlockV1[] blocks) =>
        PlayData(blocks, new Dictionary<string, string>());

    private static CustomQuizPlayData PlayData(
        CustomQuizBlockV1 first,
        CustomQuizBlockV1 second,
        IReadOnlyDictionary<string, string> resolvedValues) =>
        PlayData([first, second], resolvedValues);

    private static CustomQuizPlayData PlayData(
        IReadOnlyCollection<CustomQuizBlockV1> blocks,
        IReadOnlyDictionary<string, string> resolvedValues) => new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "Custom quiz",
            "Source quiz",
            "English",
            "Polish",
            Guid.NewGuid(),
            new CustomQuizDocumentV1 { Blocks = blocks.ToList() },
            resolvedValues);
}
