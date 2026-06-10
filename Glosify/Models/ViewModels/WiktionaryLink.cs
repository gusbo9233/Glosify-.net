namespace Glosify.Models.ViewModels;

public static class WiktionaryLink
{
    private static readonly IReadOnlyDictionary<string, string> LanguageCodes =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["English"] = "en",
            ["Estonian"] = "et",
            ["German"] = "de",
            ["Polish"] = "pl",
            ["Ukrainian"] = "uk"
        };

    public static string? ForWord(string language, string word)
    {
        if (string.IsNullOrWhiteSpace(language) || string.IsNullOrWhiteSpace(word))
        {
            return null;
        }

        if (!LanguageCodes.TryGetValue(language.Trim(), out var languageCode))
        {
            return null;
        }

        var pageName = Uri.EscapeDataString(word.Trim().Replace(' ', '_'));
        return $"https://{languageCode}.wiktionary.org/wiki/{pageName}";
    }
}
