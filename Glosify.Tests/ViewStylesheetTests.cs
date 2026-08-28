using Xunit;

namespace Glosify.Tests;

/// <summary>
/// Page-specific stylesheets are linked by the pages that need them rather than
/// by the layout, so every route stops paying for them. That only holds while
/// each page actually asks for the one it styles itself with.
/// </summary>
public sealed class ViewStylesheetTests
{
    /// <summary>
    /// Root class a stylesheet scopes itself to, and the stylesheet that defines it.
    /// </summary>
    private static readonly (string RootClass, string Stylesheet)[] PageStylesheets =
    [
        ("library-page", "css/quiz-library.css"),
        ("anki-settings-panel", "css/quiz-settings.css"),
        ("content-page", "css/payments.css"),
    ];

    [Fact]
    public void Shared_pronunciation_button_is_defined_in_the_site_stylesheet()
    {
        var siteCss = File.ReadAllText(Path.Combine(WebRootDirectory(), "css", "site.css"));

        Assert.Contains(".btn-icon-tts {", siteCss, StringComparison.Ordinal);
        Assert.Contains(".btn-icon-tts.is-playing", siteCss, StringComparison.Ordinal);
    }

    [Fact]
    public void Payment_views_use_the_button_defined_by_their_stylesheet()
    {
        var paymentsCss = File.ReadAllText(Path.Combine(WebRootDirectory(), "css", "payments.css"));
        var paymentsViews = Directory.EnumerateFiles(
            Path.Combine(ViewsDirectory(), "Payments"),
            "*.cshtml",
            SearchOption.TopDirectoryOnly);

        Assert.Contains(".payment-button {", paymentsCss, StringComparison.Ordinal);
        foreach (var view in paymentsViews)
        {
            var markup = File.ReadAllText(view);
            Assert.DoesNotContain("home-button", markup, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Pages_link_the_stylesheet_that_styles_them()
    {
        var unstyled = new List<string>();

        foreach (var (rootClass, stylesheet) in PageStylesheets)
        {
            foreach (var view in Directory.EnumerateFiles(ViewsDirectory(), "*.cshtml", SearchOption.AllDirectories))
            {
                var markup = File.ReadAllText(view);
                if (markup.Contains(rootClass, StringComparison.Ordinal)
                    && !markup.Contains(stylesheet, StringComparison.Ordinal))
                {
                    unstyled.Add($"{Path.GetFileName(view)} uses .{rootClass} without linking {stylesheet}");
                }
            }
        }

        Assert.True(unstyled.Count == 0, string.Join(Environment.NewLine, unstyled));
    }

    [Fact]
    public void The_layout_carries_only_the_stylesheet_every_page_uses()
    {
        var layout = File.ReadAllText(Path.Combine(ViewsDirectory(), "Shared", "_AppLayout.cshtml"));

        foreach (var (_, stylesheet) in PageStylesheets)
        {
            Assert.DoesNotContain(stylesheet, layout, StringComparison.Ordinal);
        }

        Assert.Contains("css/site.css", layout, StringComparison.Ordinal);
    }

    private static string ViewsDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Glosify", "Views");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Glosify Views directory.");
    }

    private static string WebRootDirectory() =>
        Path.Combine(Directory.GetParent(ViewsDirectory())!.FullName, "wwwroot");
}
