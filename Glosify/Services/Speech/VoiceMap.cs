using System.Collections.Frozen;

namespace Glosify.Services.Speech;

public static class VoiceMap
{
    private static readonly FrozenDictionary<string, (string Locale, string Voice)> Voices =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            // Supported day one:
            ["et"] = ("et-EE", "et-EE-AnuNeural"),
            ["et-ee"] = ("et-EE", "et-EE-AnuNeural"),
            ["estonian"] = ("et-EE", "et-EE-AnuNeural"),

            ["de"] = ("de-DE", "de-DE-KatjaNeural"),
            ["de-de"] = ("de-DE", "de-DE-KatjaNeural"),
            ["german"] = ("de-DE", "de-DE-KatjaNeural"),

            ["pl"] = ("pl-PL", "pl-PL-AgnieszkaNeural"),
            ["pl-pl"] = ("pl-PL", "pl-PL-AgnieszkaNeural"),
            ["polish"] = ("pl-PL", "pl-PL-AgnieszkaNeural"),

            ["uk"] = ("uk-UA", "uk-UA-PolinaNeural"),
            ["uk-ua"] = ("uk-UA", "uk-UA-PolinaNeural"),
            ["ukrainian"] = ("uk-UA", "uk-UA-PolinaNeural"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string languageCode, out string locale, out string voice)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)
            && Voices.TryGetValue(languageCode.Trim(), out var value))
        {
            locale = value.Locale;
            voice = value.Voice;
            return true;
        }

        locale = string.Empty;
        voice = string.Empty;
        return false;
    }
}
