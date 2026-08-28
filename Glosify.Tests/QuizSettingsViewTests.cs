using Xunit;

namespace Glosify.Tests;

public sealed class QuizSettingsViewTests
{
    [Fact]
    public void Settings_contains_only_quiz_session_configuration()
    {
        var markup = File.ReadAllText(Path.Combine(ViewsDirectory(), "Quiz", "Settings.cshtml"));

        Assert.Contains("asp-action=\"Start\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("asp-controller=\"Anki\"", markup, StringComparison.Ordinal);
        Assert.DoesNotContain("anki-settings", markup, StringComparison.OrdinalIgnoreCase);
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
