using System.Globalization;
using Glosify.Services.Language;

namespace Glosify.Localization;

public static class QuizLanguageDisplay
{
    public static string Name(QuizLanguage language) =>
        CultureInfo.CurrentUICulture.Name != DisplayCultureCatalog.DefaultCulture
            ? LocalizedCultureName(language)
            : language.Name;

    public static string Name(string? storedName)
    {
        var language = QuizLanguageCatalog.Find(storedName);
        return language is null ? storedName ?? string.Empty : Name(language);
    }

    private static string LocalizedCultureName(QuizLanguage language)
    {
        try
        {
            var specific = CultureInfo.GetCultureInfo(language.Locale);
            var neutral = specific.Parent;
            return string.IsNullOrWhiteSpace(neutral.DisplayName)
                ? language.NativeName
                : neutral.DisplayName;
        }
        catch (CultureNotFoundException)
        {
            return language.NativeName;
        }
    }
}
