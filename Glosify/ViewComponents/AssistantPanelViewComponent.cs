using Glosify.Models.ViewModels;
using Microsoft.AspNetCore.Mvc;

namespace Glosify.ViewComponents;

/// <summary>
/// Renders the assistant shell. The context library is intentionally loaded by the
/// browser only after the learner opens the panel.
/// </summary>
public sealed class AssistantPanelViewComponent : ViewComponent
{
    public IViewComponentResult Invoke(AssistantPanelViewModel panel) =>
        View(new AssistantPanelContentViewModel
        {
            Panel = panel,
            ContextOptionsUrl = Url.Action("Get", "AssistantContext")
                ?? "/Assistant/ContextOptions",
        });
}
