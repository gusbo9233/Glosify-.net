using Glosify.Data;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddSubmitButtonTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_submit_button",
        "Add the custom quiz's single submit button. Call exactly once per new quiz.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["text"] = StringProp("Button text, such as Check answers."),
        }, ["id"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddSubmitButtonTool(GlosifyContext context, CustomQuizToolStore customQuizzes)
        : base(context, customQuizzes)
    {
    }
}
