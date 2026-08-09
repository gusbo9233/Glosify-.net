using Glosify.Data;
using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddFeedbackMessageTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_feedback_message",
        "Add the custom quiz's single feedback message element. Call exactly once per new quiz.",
        new Dictionary<string, object> { ["id"] = StringProp("Stable unique element id.") }, ["id"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddFeedbackMessageTool(GlosifyContext context, CustomQuizToolStore customQuizzes)
        : base(context, customQuizzes)
    {
    }
}
