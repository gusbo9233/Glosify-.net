using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddWordBankTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_word_bank",
        "Add one word bank targeting existing text inputs or textareas.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["target_input_ids"] = StringArrayProp("Ids of text inputs or textareas filled by this word bank."),
        }, ["id", "target_input_ids"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddWordBankTool(CustomQuizToolStore customQuizzes)
        : base(customQuizzes)
    {
    }
}
