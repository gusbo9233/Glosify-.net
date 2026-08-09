using Glosify.Data;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddCheckboxTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_checkbox",
        "Add one graded checkbox to the custom quiz. Call once per checkbox question.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("Specific learner-visible checkbox question."),
            ["expected_checked"] = new Dictionary<string, object> { ["type"] = "boolean" },
        }, ["id", "label", "expected_checked"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddCheckboxTool(GlosifyContext context, CustomQuizToolStore customQuizzes)
        : base(context, customQuizzes)
    {
    }
}
