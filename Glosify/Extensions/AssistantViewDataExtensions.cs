using Glosify.Models.ViewModels;
using Microsoft.AspNetCore.Mvc.ViewFeatures;

namespace Glosify.Extensions;

/// <summary>
/// Typed boundary between feature views and the shared application layout.
/// </summary>
public static class AssistantViewDataExtensions
{
    private const string ContextKey = "__GlosifyAssistantPageContext";

    public static void SetAssistantPageContext(
        this ViewDataDictionary viewData,
        AssistantPageContext context) =>
        viewData[ContextKey] = context;

    public static void HideAssistantPanel(this ViewDataDictionary viewData) =>
        viewData.SetAssistantPageContext(AssistantPageContext.Hidden);

    public static AssistantPageContext? GetAssistantPageContext(this ViewDataDictionary viewData) =>
        viewData[ContextKey] as AssistantPageContext;
}
