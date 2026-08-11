using System.Text.RegularExpressions;
using Glosify.Services.CustomQuizzes;
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

    private static readonly Regex QuotedName = new(@"""([a-z0-9_]+)""", RegexOptions.Compiled);

    /// <summary>
    /// Icons a script assigns through <c>textContent</c> after building the
    /// element, which no pattern over the markup can see. Each is listed with
    /// the call site that produces it so the pairing can be re-checked by hand.
    /// </summary>
    private static readonly (string Icon, string Source)[] ScriptAssignedIcons =
    [
        ("local_bar", "speaking.js — drink shelf, non-spirit"),
        ("shot_bar", "speaking.js — drink shelf, spirit"),
        ("forum", "classroom-chat.js — empty chat"),
        ("mic", "classroom-call.js — setControlState(muteButton, ...)"),
        ("mic_off", "classroom-call.js — muted participant and setControlState"),
        ("thumb_down", "assistant.js — feedback vote"),
        ("thumb_up", "assistant.js — feedback vote"),
        ("videocam", "classroom-call.js — setControlState(cameraButton, ...)"),
        ("videocam_off", "classroom-call.js — setControlState(cameraButton, ...)"),
    ];

    /// <summary>
    /// The custom-quiz templates name their own icon in C#, so the catalog is
    /// asked rather than pattern-matched — adding a template keeps working.
    /// </summary>
    private static IEnumerable<(string Icon, string Source)> CatalogIcons() =>
        new CustomQuizTemplateCatalog().List()
            .Select(template => (template.Icon, $"CustomQuizTemplateCatalog — {template.Name}"));

    [Fact]
    public void Every_icon_the_app_renders_is_in_the_requested_subset()
    {
        var subset = WebFonts.IconNames.ToHashSet(StringComparer.Ordinal);
        var missing = new SortedDictionary<string, string>(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);

            foreach (Match match in LiteralLigature.Matches(text))
            {
                Record(match.Groups[1].Value, file);
            }

            foreach (Match match in ConditionalLigature.Matches(text))
            {
                foreach (Match name in QuotedName.Matches(match.Groups[1].Value))
                {
                    Record(name.Groups[1].Value, file);
                }
            }
        }

        foreach (var (icon, source) in ScriptAssignedIcons.Concat(CatalogIcons()))
        {
            Record(icon, source);
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
        var used = ScriptAssignedIcons.Concat(CatalogIcons())
            .Select(entry => entry.Icon)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var file in SourceFiles())
        {
            var text = File.ReadAllText(file);
            used.UnionWith(LiteralLigature.Matches(text).Select(match => match.Groups[1].Value));
            used.UnionWith(ConditionalLigature.Matches(text)
                .SelectMany(match => QuotedName.Matches(match.Groups[1].Value))
                .Select(name => name.Groups[1].Value));
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
