using Glosify.Data;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddCustomQuizElementTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "add_custom_quiz_element",
        "Add exactly one custom quiz element not covered by a more specific add tool. Never pass an array or a complete quiz document. Queued until Apply.",
        BuildSchema(new Dictionary<string, object>
        {
            ["custom_quiz_id"] = StringProp("Optional existing custom quiz id. Omit for the open quiz or the new quiz shell started earlier in this turn."),
            ["element"] = CustomQuizBlockProp(useWordReference: false, requireType: true),
        }, required: ["element"]));

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddCustomQuizElementTool(GlosifyContext context, CustomQuizToolStore customQuizzes)
        : base(context, customQuizzes)
    {
    }
}
