using Glosify.Services.Ai.Generation;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class AddTextInputTool : AtomicCustomQuizElementTool
{
    private static readonly AgentToolDeclaration DeclarationValue = AtomicElementDeclaration(
        "add_text_input",
        "Add one graded text answer to the custom quiz. Call once per question. Single-line inputs render as compact inline blanks: put {{blank}} in the learner-visible label exactly where the answer belongs. Never draw a blank with underscores or dots. Use expected_text for literal endings; otherwise use expected_binding.",
        new Dictionary<string, object>
        {
            ["id"] = StringProp("Stable unique element id."),
            ["label"] = StringProp("The complete, specific exercise row with {{blank}} at the answer position, for example '1. ja jest{{blank}}'. Do not include underscores or a second visual blank."),
            ["answer_type"] = EnumProp("Use text_input for one line or textarea for a long answer.", "text_input", "textarea"),
            ["expected_text"] = StringProp("Literal correct answer, such as a verb ending."),
            ["expected_binding"] = FlexibleBindingProp(),
        }, ["id", "label"]);

    public override AgentToolDeclaration Declaration => DeclarationValue;

    public AddTextInputTool(CustomQuizToolStore customQuizzes)
        : base(customQuizzes)
    {
    }
}
