using Glosify.Services.Guest;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.AspNetCore.Razor.TagHelpers;

namespace Glosify.TagHelpers;

/// <summary>Routes bundled examples through real views without granting access to owned resources.</summary>
[HtmlTargetElement("a")]
[HtmlTargetElement("form")]
[HtmlTargetElement("button", Attributes = "data-modal-open")]
[HtmlTargetElement("button", Attributes = "data-open-anki-item")]
[HtmlTargetElement("button", Attributes = "data-translation-toggle")]
[HtmlTargetElement("canvas")]
[HtmlTargetElement("script")]
public sealed class GuestSampleTagHelper : TagHelper
{
    [ViewContext, HtmlAttributeNotBound] public ViewContext ViewContext { get; set; } = null!;
    public override int Order => 1000;

    public override void Process(TagHelperContext context, TagHelperOutput output)
    {
        if (ViewContext.ViewData["GuestSample"] is not string section) return;
        string Attribute(string name) => context.AllAttributes[name]?.Value?.ToString() ?? "";
        var action = Attribute("asp-action").ToLowerInvariant();
        var controller = Attribute("asp-controller").ToLowerInvariant();
        var destination = GuestSamples.Find(controller)?.Key ?? section;
        if (output.TagName == "a")
        {
            if (controller == "languages" && action == "index")
            {
                var request = ViewContext.HttpContext.Request;
                output.Attributes.SetAttribute("href", "/Languages?returnUrl=" + Uri.EscapeDataString(request.PathBase + request.Path + request.QueryString));
                return;
            }
            string? href = action switch
            {
                "index" => GuestSamples.Library(destination).ReturnUrl,
                "details" or "read" => $"/preview/{destination}/sample",
                "settings" when destination == "quiz" => "/preview/quiz/sample/settings",
                _ => null
            };
            if (href is not null && (string.IsNullOrEmpty(controller) || GuestSamples.Find(controller) is not null))
                output.Attributes.SetAttribute("href", href);
        }
        // Shared chrome (display language, sign-out) opts out with data-sample-passthrough and
        // keeps posting to its real endpoint. Every other form still defaults to the login prompt.
        if (output.TagName == "form" && string.Equals(Attribute("method"), "post", StringComparison.OrdinalIgnoreCase)
            && !context.AllAttributes.ContainsName("data-sample-passthrough"))
        {
            var classes = Attribute("class");
            var restart = Attribute("asp-controller") == "TypingQuiz" && Attribute("asp-action") == "Restart";
            if (restart) output.Attributes.SetAttribute("data-sample-restart-form", "");
            var practice = context.AllAttributes.ContainsName("data-flashcard-form") || classes == "session-reset" || restart;
            if (classes == "settings-shell") output.Attributes.SetAttribute("data-sample-settings", "");
            else if (!practice)
            {
                output.Attributes.SetAttribute("data-sample-account-form", "");
                // A no-script submission still reaches login, never a sample mutation.
                output.Attributes.SetAttribute("action", "/login");
                output.Attributes.SetAttribute("method", "get");
                output.PostContent.AppendHtml($"<input type=\"hidden\" name=\"returnUrl\" value=\"{GuestSamples.Library(section).ReturnUrl}\" />");
            }
        }
        if (output.TagName == "button" && (context.AllAttributes.ContainsName("data-modal-open") || context.AllAttributes.ContainsName("data-open-anki-item") || context.AllAttributes.ContainsName("data-translation-toggle")))
            output.Attributes.SetAttribute("data-login-required", "");
        if (output.TagName == "canvas" && context.AllAttributes.ContainsName("data-book-preview"))
            output.Attributes.SetAttribute("data-pdf-url", GuestSamples.For(ViewContext.HttpContext).PdfUrl);
        if (output.TagName == "script" && (Attribute("src").Contains("flashcard-quiz.js", StringComparison.Ordinal) || Attribute("src").Contains("typing-quiz.js", StringComparison.Ordinal)))
            output.SuppressOutput();
    }
}
