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
    ];

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
}
