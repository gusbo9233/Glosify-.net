using System.Text.RegularExpressions;
using Xunit;

namespace Glosify.Tests;

/// <summary>
/// The layouts ask Google for a subset of Material Symbols rather than the whole
/// 1.1 MB font, so an icon the app renders but <see cref="WebFonts.IconNames"/>
/// does not list arrives as its own name in plain text. This scans the sources
/// for icon usages and fails before that reaches a page.
/// </summary>
public sealed class MaterialSymbolInventoryTests
{
    /// <summary>
    /// A ligature written directly inside a Material Symbols element, which is
    /// how views and the scripts that build markup from strings spell an icon.
    /// </summary>
    private static readonly Regex LiteralLigature = new(
        @"material-symbols-outlined[^>]*>\s*([a-z0-9_]+)\s*<",
        RegexOptions.Compiled);

    /// <summary>
    /// Razor picking between icons inline, e.g. `@(isPublic ? "lock" : "public")`.
    /// </summary>
    private static readonly Regex ConditionalLigature = new(
        @"material-symbols-outlined[^>]*>\s*@\(([^)]*)\)",
        RegexOptions.Compiled);

    private static readonly Regex RazorQuotedName = new(@"""([a-z0-9_]+)""", RegexOptions.Compiled);

    private static readonly Regex JavaScriptIconProperty = new(
        @"\bicon\s*:\s*['""]([a-z0-9_]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptIconButton = new(
        @"\biconButton\s*\(\s*['""]([a-z0-9_]+)['""]",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptIconTextContent = new(
        @"\b(?:[A-Za-z_$][\w$]*)?[Ii]con\.textContent\s*=\s*([^;]+)",
        RegexOptions.Compiled);

    private static readonly Regex JavaScriptQuotedName = new(
        @"['""]([a-z0-9_]+)['""]",
        RegexOptions.Compiled);

    [Fact]
    public void Every_icon_the_app_renders_is_in_the_requested_subset()
    {
        var subset = WebFonts.IconNames.ToHashSet(StringComparer.Ordinal);
        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (var icon in IconsIn(text))
            {
                Record(icon, file);
            }
        }

        Assert.True(
            missing.Count == 0,
            "These Material Symbols are used but not requested, so they will render as "
            + "their own names. Add them to WebFonts.IconNames:"
            + string.Concat(missing.Select(entry => $"{Environment.NewLine}  {entry.Key} — {entry.Value}")));

        void Record(string icon, string source)
        {
            if (!subset.Contains(icon))
            {
                missing.TryAdd(icon, source);
            }
        }
    }

    [Fact]
    public void The_requested_subset_carries_nothing_the_app_stopped_using()
    {
        var used = new HashSet<string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            used.UnionWith(IconsIn(text));
        }

        var unused = WebFonts.IconNames.Where(icon => !used.Contains(icon)).ToArray();

        Assert.True(
            unused.Length == 0,
            $"WebFonts.IconNames requests icons nothing renders: {string.Join(", ", unused)}");
    }

    [Fact]
    public void The_requested_subset_is_sorted_and_free_of_duplicates()
    {
        // Google rejects the request outright when icon_names is unsorted, and a
        // duplicate would only bloat an already long URL.
        Assert.Equal(WebFonts.IconNames.Order(StringComparer.Ordinal).ToArray(), WebFonts.IconNames);
        Assert.Equal(WebFonts.IconNames.Length, WebFonts.IconNames.Distinct(StringComparer.Ordinal).Count());
    }

    public static TheoryData<string, string[]> DynamicIconExamples => new()
    {
        { "<span class=\"material-symbols-outlined\">search</span>", ["search"] },
        { "<span class=\"material-symbols-outlined\">@(visible ? \"lock\" : \"public\")</span>", ["lock", "public"] },
        { "const state = { icon: 'progress_activity' };", ["progress_activity"] },
        { "iconButton('arrow_downward', 'Move down', action);", ["arrow_downward"] },
        { "readerTtsIcon.textContent = playing ? 'stop_circle' : 'volume_up';", ["stop_circle", "volume_up"] },
        { "icon.textContent = 'auto_awesome';", ["auto_awesome"] },
    };

    [Theory]
    [MemberData(nameof(DynamicIconExamples))]
    public void Dynamic_icon_scanner_recognizes_supported_source_forms(string source, string[] expected)
    {
        Assert.Equal(expected.Order(StringComparer.Ordinal), IconsIn(source).Order(StringComparer.Ordinal));
    }

    [Fact]
    public void Explore_community_badge_uses_a_loaded_symbol_instead_of_leaking_a_ligature()
    {
        var view = File.ReadAllText(Path.Combine(WebProjectDirectory(), "Views", "Explore", "Index.cshtml"));
        var styles = File.ReadAllText(Path.Combine(WebProjectDirectory(), "wwwroot", "css", "quiz-library.css"));

        Assert.Contains(">group</span>", view, StringComparison.Ordinal);
        Assert.DoesNotContain(">groups</span>", view, StringComparison.Ordinal);
        Assert.DoesNotContain("diversity_3", view, StringComparison.Ordinal);
        Assert.Contains(".library-page .explore-hero-feature div > span", styles, StringComparison.Ordinal);
        Assert.DoesNotContain(".library-page .explore-hero-feature span {", styles, StringComparison.Ordinal);
    }

    private static IEnumerable<string> SourceFiles()
    {
        var web = WebProjectDirectory();
        string[] roots = [Path.Combine(web, "Views"), Path.Combine(web, "wwwroot", "js")];

        return roots
            .Where(Directory.Exists)
            .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cshtml", StringComparison.OrdinalIgnoreCase)
                || file.EndsWith(".js", StringComparison.OrdinalIgnoreCase));
    }

    private static IEnumerable<string> IconsIn(string text)
    {
        foreach (Match match in LiteralLigature.Matches(text))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in ConditionalLigature.Matches(text))
        {
            foreach (Match name in RazorQuotedName.Matches(match.Groups[1].Value))
            {
                yield return name.Groups[1].Value;
            }
        }

        foreach (Match match in JavaScriptIconProperty.Matches(text))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in JavaScriptIconButton.Matches(text))
        {
            yield return match.Groups[1].Value;
        }

        foreach (Match match in JavaScriptIconTextContent.Matches(text))
        {
            foreach (Match name in JavaScriptQuotedName.Matches(match.Groups[1].Value))
            {
                yield return name.Groups[1].Value;
            }
        }
    }

    private static string WebProjectDirectory()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Glosify", "Glosify.csproj");
            if (File.Exists(candidate))
            {
                return Path.Combine(directory.FullName, "Glosify");
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate the Glosify web project from the test output directory.");
    }
}
