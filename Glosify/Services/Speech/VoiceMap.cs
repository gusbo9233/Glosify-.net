using System.Collections.Frozen;
using Glosify.Services.Language;

namespace Glosify.Services.Speech;

public static class VoiceMap
{
    // Curated standard neural voices from Microsoft's Azure Speech language table.
    // Keep these deterministic: selecting the first voice returned by the regional
    // voices API would let an upstream ordering change alter Glosify's pronunciation.
    private static readonly FrozenDictionary<string, (string Locale, string Voice)> Voices =
        new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase)
        {
            ["af"] = ("af-ZA", "af-ZA-AdriNeural"),
            ["ar"] = ("ar-SA", "ar-SA-ZariyahNeural"),
            ["as"] = ("as-IN", "as-IN-YashicaNeural"),
            ["az"] = ("az-AZ", "az-AZ-BanuNeural"),
            ["bg"] = ("bg-BG", "bg-BG-KalinaNeural"),
            ["bn"] = ("bn-BD", "bn-BD-NabanitaNeural"),
            ["bs"] = ("bs-BA", "bs-BA-VesnaNeural"),
            ["ca"] = ("ca-ES", "ca-ES-JoanaNeural"),
            ["cs"] = ("cs-CZ", "cs-CZ-VlastaNeural"),
            ["cy"] = ("cy-GB", "cy-GB-NiaNeural"),
            ["da"] = ("da-DK", "da-DK-ChristelNeural"),
            ["de"] = ("de-DE", "de-DE-KatjaNeural"),
            ["el"] = ("el-GR", "el-GR-AthinaNeural"),
            ["en"] = ("en-GB", "en-GB-SoniaNeural"),
            ["es"] = ("es-ES", "es-ES-ElviraNeural"),
            ["et"] = ("et-EE", "et-EE-AnuNeural"),
            ["fa"] = ("fa-IR", "fa-IR-DilaraNeural"),
            ["fi"] = ("fi-FI", "fi-FI-SelmaNeural"),
            ["fil"] = ("fil-PH", "fil-PH-BlessicaNeural"),
            ["fr"] = ("fr-FR", "fr-FR-DeniseNeural"),
            ["gl"] = ("gl-ES", "gl-ES-SabelaNeural"),
            ["gu"] = ("gu-IN", "gu-IN-DhwaniNeural"),
            ["he"] = ("he-IL", "he-IL-HilaNeural"),
            ["hi"] = ("hi-IN", "hi-IN-AaravNeural"),
            ["hr"] = ("hr-HR", "hr-HR-GabrijelaNeural"),
            ["hu"] = ("hu-HU", "hu-HU-NoemiNeural"),
            ["hy"] = ("hy-AM", "hy-AM-AnahitNeural"),
            ["id"] = ("id-ID", "id-ID-GadisNeural"),
            ["is"] = ("is-IS", "is-IS-GudrunNeural"),
            ["it"] = ("it-IT", "it-IT-ElsaNeural"),
            ["ja"] = ("ja-JP", "ja-JP-NanamiNeural"),
            ["ka"] = ("ka-GE", "ka-GE-EkaNeural"),
            ["kk"] = ("kk-KZ", "kk-KZ-AigulNeural"),
            ["kn"] = ("kn-IN", "kn-IN-SapnaNeural"),
            ["ko"] = ("ko-KR", "ko-KR-SunHiNeural"),
            ["lt"] = ("lt-LT", "lt-LT-OnaNeural"),
            ["lv"] = ("lv-LV", "lv-LV-EveritaNeural"),
            ["mk"] = ("mk-MK", "mk-MK-MarijaNeural"),
            ["ml"] = ("ml-IN", "ml-IN-SobhanaNeural"),
            ["mr"] = ("mr-IN", "mr-IN-AarohiNeural"),
            ["ms"] = ("ms-MY", "ms-MY-YasminNeural"),
            ["mt"] = ("mt-MT", "mt-MT-GraceNeural"),
            ["my"] = ("my-MM", "my-MM-NilarNeural"),
            ["nb"] = ("nb-NO", "nb-NO-PernilleNeural"),
            ["ne"] = ("ne-NP", "ne-NP-HemkalaNeural"),
            ["nl"] = ("nl-NL", "nl-NL-FennaNeural"),
            ["or"] = ("or-IN", "or-IN-SubhasiniNeural"),
            ["pa"] = ("pa-IN", "pa-IN-OjasNeural"),
            ["pl"] = ("pl-PL", "pl-PL-ZofiaNeural"),
            ["pt"] = ("pt-BR", "pt-BR-FranciscaNeural"),
            ["ro"] = ("ro-RO", "ro-RO-AlinaNeural"),
            ["ru"] = ("ru-RU", "ru-RU-SvetlanaNeural"),
            ["sk"] = ("sk-SK", "sk-SK-ViktoriaNeural"),
            ["sl"] = ("sl-SI", "sl-SI-PetraNeural"),
            ["sr-Latn"] = ("sr-Latn-RS", "sr-Latn-RS-NicholasNeural"),
            ["sv"] = ("sv-SE", "sv-SE-SofieNeural"),
            ["sw"] = ("sw-TZ", "sw-TZ-RehemaNeural"),
            ["ta"] = ("ta-IN", "ta-IN-PallaviNeural"),
            ["te"] = ("te-IN", "te-IN-ShrutiNeural"),
            ["th"] = ("th-TH", "th-TH-PremwadeeNeural"),
            ["tr"] = ("tr-TR", "tr-TR-EmelNeural"),
            ["uk"] = ("uk-UA", "uk-UA-PolinaNeural"),
            ["uz"] = ("uz-UZ", "uz-UZ-MadinaNeural"),
            ["vi"] = ("vi-VN", "vi-VN-HoaiMyNeural"),
            // Azure publishes its Hong Kong Cantonese voices under zh-HK.
            ["yue"] = ("zh-HK", "zh-HK-HiuMaanNeural"),
            ["zh-Hans"] = ("zh-CN", "zh-CN-XiaoxiaoNeural"),
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
        }.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, string> ClientLocaleAliases { get; } =
        BuildClientLocaleAliases();

    public static bool TryResolve(string languageCode, out string voice)
        => TryResolve(languageCode, voicePreference: null, out _, out voice);

    public static bool TryResolve(
        string languageCode,
        string? voicePreference,
        out string locale,
        out string voice)
    {
        var code = ResolveCatalogCode(languageCode);
        if (code is not null && Voices.TryGetValue(code, out var value))
        {
            locale = value.Locale;
            voice = value.Voice;
            if (string.Equals(code, "pl", StringComparison.OrdinalIgnoreCase)
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
        var code = ResolveCatalogCode(languageCode);
        if (code is not null && HighDefinitionVoices.TryGetValue(code, out var value))
        {
            voice = value;
            return true;
        }

        voice = string.Empty;
        return false;
    }

    private static string? ResolveCatalogCode(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var candidate = value.Trim();
        return QuizLanguageCatalog.Find(candidate)?.Code
            ?? QuizLanguageCatalog.LanguageLearning.FirstOrDefault(language =>
                string.Equals(language.Locale, candidate, StringComparison.OrdinalIgnoreCase))?.Code;
    }

    private static FrozenDictionary<string, string> BuildClientLocaleAliases()
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var language in QuizLanguageCatalog.LanguageLearning)
        {
            var locale = Voices.TryGetValue(language.Code, out var configured)
                ? configured.Locale
                : language.Locale;
            AddAlias(aliases, language.Code, locale);
            AddAlias(aliases, language.TranslatorCode, locale);
            AddAlias(aliases, language.ScribeCode, locale);
            AddAlias(aliases, language.Name, locale);
            AddAlias(aliases, language.NativeName, locale);
            AddAlias(aliases, language.Locale, locale);
            foreach (var alias in language.Aliases)
            {
                AddAlias(aliases, alias, locale);
            }
        }

        return aliases.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static void AddAlias(IDictionary<string, string> aliases, string? alias, string locale)
    {
        if (!string.IsNullOrWhiteSpace(alias))
        {
            aliases.TryAdd(alias.Trim().ToLowerInvariant(), locale);
        }
    }
}
