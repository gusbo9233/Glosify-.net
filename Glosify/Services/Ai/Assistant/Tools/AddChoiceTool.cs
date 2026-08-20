using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddChoiceTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_choice",
        "Add one graded choice question. Call once per question. Supply at least two options and mark the correct selection or selections.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("Specific learner-visible choice question."),
            ["choice_type"] = EnumProp("Choice control type.", "radio_group", "multi_select_group", "select_menu"),
            ["options"] = FlexibleOptionsProp(),
        }, ["id", "label", "choice_type", "options"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddChoiceTool(CustomQuizToolStore customQuizzes)
        : base(customQuizzes)
    {
    }
}
