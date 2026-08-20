using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddLabelTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_label",
        "Add one visible label to the custom quiz. Call once per label. label_type may be instruction_label, prompt_label, translation_label, or quiz_heading.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["text"] = StringProp("Visible label text."),
            ["label_type"] = EnumProp("Label type.", "instruction_label", "prompt_label", "translation_label", "quiz_heading"),
        }, ["id", "text"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddLabelTool(CustomQuizToolStore customQuizzes)
        : base(customQuizzes)
    {
    }
}
