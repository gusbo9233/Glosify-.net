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

            ["pl"] = ("pl-PL", "pl-PL-ZofiaNeural"),
            ["pl-pl"] = ("pl-PL", "pl-PL-ZofiaNeural"),
            ["polish"] = ("pl-PL", "pl-PL-ZofiaNeural"),

            ["uk"] = ("uk-UA", "uk-UA-PolinaNeural"),
            ["uk-ua"] = ("uk-UA", "uk-UA-PolinaNeural"),
            ["ukrainian"] = ("uk-UA", "uk-UA-PolinaNeural"),
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> PolishVoices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["zofia"] = "pl-PL-ZofiaNeural",
            ["agnieszka"] = "pl-PL-AgnieszkaNeural",
            ["marek"] = "pl-PL-MarekNeural",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, string> HighDefinitionVoices =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            // Only use voices explicitly listed by Azure for the locale. Azure can
            // accept an Omni suffix for other voices while producing poor speech.
            ["de"] = "de-DE-Seraphina:DragonHDLatestNeural",
            ["de-de"] = "de-DE-Seraphina:DragonHDLatestNeural",
            ["german"] = "de-DE-Seraphina:DragonHDLatestNeural",
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static bool TryResolve(string languageCode, out string locale, out string voice)
        => TryResolve(languageCode, voicePreference: null, out locale, out voice);

    public static bool TryResolve(
        string languageCode,
        string? voicePreference,
        out string locale,
        out string voice)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)
            && Voices.TryGetValue(languageCode.Trim(), out var value))
        {
            locale = value.Locale;
            voice = value.Voice;
            if (string.Equals(locale, "pl-PL", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(voicePreference)
                && PolishVoices.TryGetValue(voicePreference.Trim(), out var preferredVoice))
            {
                voice = preferredVoice;
            }
            return true;
        }

        locale = string.Empty;
        voice = string.Empty;
        return false;
    }

    public static bool TryResolveHighDefinition(string languageCode, out string voice)
    {
        if (!string.IsNullOrWhiteSpace(languageCode)
            && HighDefinitionVoices.TryGetValue(languageCode.Trim(), out var value))
        {
            voice = value;
            return true;
        }

        voice = string.Empty;
        return false;
    }
}
