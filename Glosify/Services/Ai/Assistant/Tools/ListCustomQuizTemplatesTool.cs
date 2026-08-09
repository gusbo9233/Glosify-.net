using System.Text.Json;
using Glosify.Services.Ai.Generation;
using Glosify.Services.CustomQuizzes;
using static Glosify.Services.Ai.Assistant.Tools.ToolSchema;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed class ListCustomQuizTemplatesTool : IAssistantTool
{
    private static readonly AgentToolDeclaration DeclarationValue = new(
        "list_custom_quiz_templates",
        "List curated visual and layout templates for custom quizzes. Use this before creating or substantially redesigning a custom quiz, then follow the selected template's layout guidance while adding individual elements.",
        BuildSchema([]));

    public AgentToolDeclaration Declaration => DeclarationValue;

    public Task<object> ExecuteAsync(
        JsonElement args,
        AgentToolContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(ListCustomQuizTemplates());

    private static object ListCustomQuizTemplates()
    {
        var templates = new CustomQuizTemplateCatalog().List().Select(template => new
        {
            id = template.Id,
            name = template.Name,
            description = template.Description,
            style_preset = template.StylePreset,
            best_for = template.BestFor,
            layout_guidance = template.LayoutGuidance,
        }).ToList();
        return new { templates, count = templates.Count };
    }
}
