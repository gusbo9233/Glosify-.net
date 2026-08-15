using Glosify.Data;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace Glosify.Services.Language;

public sealed record QuizLanguage
{
    public QuizLanguage(
        string code,
        string translatorCode,
        string scribeCode,
        string name,
        string nativeName,
        string locale,
        string flagRegion,
        params string[] aliases)
    {
        Code = code;
        TranslatorCode = translatorCode;
        ScribeCode = scribeCode;
        Name = name;
        NativeName = nativeName;
        Locale = locale;
        FlagRegion = flagRegion;
        Aliases = aliases;
    }

    public string Code { get; }
    public string TranslatorCode { get; }
    public string ScribeCode { get; }
    public string Name { get; }
    public string NativeName { get; }
    public string Locale { get; }
    public string FlagRegion { get; }
    public IReadOnlyList<string> Aliases { get; }

    public string Flag => string.Concat(FlagRegion.ToUpperInvariant().Select(character =>
        char.ConvertFromUtf32(0x1F1E6 + character - 'A')));
}

public static class QuizLanguageCatalog
{
    public const string Version = "2026-08-15";

    private static readonly QuizLanguage[] Languages =
    [
        new("af", "af", "afr", "Afrikaans", "Afrikaans", "af-ZA", "ZA"),
        new("ar", "ar", "ara", "Arabic", "العربية", "ar-SA", "SA"),
        new("hy", "hy", "hye", "Armenian", "Հայերեն", "hy-AM", "AM"),
        new("as", "as", "asm", "Assamese", "অসমীয়া", "as-IN", "IN"),
        new("az", "az", "aze", "Azerbaijani", "Azərbaycanca", "az-AZ", "AZ", "Azeri"),
        new("bn", "bn", "ben", "Bangla", "বাংলা", "bn-BD", "BD", "Bengali"),
        new("bs", "bs", "bos", "Bosnian", "Bosanski", "bs-BA", "BA"),
        new("bg", "bg", "bul", "Bulgarian", "Български", "bg-BG", "BG"),
        new("my", "my", "mya", "Burmese", "မြန်မာဘာသာ", "my-MM", "MM", "Myanmar"),
        new("yue", "yue", "yue", "Cantonese", "粵語", "yue-HK", "HK", "Cantonese (Traditional)"),
        new("ca", "ca", "cat", "Catalan", "Català", "ca-ES", "ES"),
        new("zh-Hans", "zh-Hans", "zho", "Chinese (Simplified)", "简体中文", "zh-CN", "CN", "zh", "Chinese", "Mandarin", "Mandarin Chinese", "Simplified Chinese"),
        new("hr", "hr", "hrv", "Croatian", "Hrvatski", "hr-HR", "HR"),
        new("cs", "cs", "ces", "Czech", "Čeština", "cs-CZ", "CZ"),
        new("da", "da", "dan", "Danish", "Dansk", "da-DK", "DK"),
        new("nl", "nl", "nld", "Dutch", "Nederlands", "nl-NL", "NL"),
        new("en", "en", "eng", "English", "English", "en-GB", "GB"),
        new("et", "et", "est", "Estonian", "Eesti", "et-EE", "EE"),
        new("fil", "fil", "fil", "Filipino", "Filipino", "fil-PH", "PH", "Tagalog"),
        new("fi", "fi", "fin", "Finnish", "Suomi", "fi-FI", "FI"),
        new("fr", "fr", "fra", "French", "Français", "fr-FR", "FR"),
        new("gl", "gl", "glg", "Galician", "Galego", "gl-ES", "ES"),
        new("ka", "ka", "kat", "Georgian", "ქართული", "ka-GE", "GE"),
        new("de", "de", "deu", "German", "Deutsch", "de-DE", "DE"),
        new("el", "el", "ell", "Greek", "Ελληνικά", "el-GR", "GR"),
        new("gu", "gu", "guj", "Gujarati", "ગુજરાતી", "gu-IN", "IN"),
        new("ha", "ha", "hau", "Hausa", "Hausa", "ha-NG", "NG"),
        new("he", "he", "heb", "Hebrew", "עברית", "he-IL", "IL"),
        new("hi", "hi", "hin", "Hindi", "हिन्दी", "hi-IN", "IN"),
        new("hu", "hu", "hun", "Hungarian", "Magyar", "hu-HU", "HU"),
        new("is", "is", "isl", "Icelandic", "Íslenska", "is-IS", "IS"),
        new("id", "id", "ind", "Indonesian", "Bahasa Indonesia", "id-ID", "ID"),
        new("it", "it", "ita", "Italian", "Italiano", "it-IT", "IT"),
        new("ja", "ja", "jpn", "Japanese", "日本語", "ja-JP", "JP"),
        new("kn", "kn", "kan", "Kannada", "ಕನ್ನಡ", "kn-IN", "IN"),
        new("kk", "kk", "kaz", "Kazakh", "Қазақша", "kk-KZ", "KZ"),
        new("ko", "ko", "kor", "Korean", "한국어", "ko-KR", "KR"),
        new("ky", "ky", "kir", "Kyrgyz", "Кыргызча", "ky-KG", "KG"),
        new("lv", "lv", "lav", "Latvian", "Latviešu", "lv-LV", "LV"),
        new("lt", "lt", "lit", "Lithuanian", "Lietuvių", "lt-LT", "LT"),
        new("mk", "mk", "mkd", "Macedonian", "Македонски", "mk-MK", "MK"),
        new("ms", "ms", "msa", "Malay", "Bahasa Melayu", "ms-MY", "MY"),
        new("ml", "ml", "mal", "Malayalam", "മലയാളം", "ml-IN", "IN"),
        new("mt", "mt", "mlt", "Maltese", "Malti", "mt-MT", "MT"),
        new("mi", "mi", "mri", "Māori", "Māori", "mi-NZ", "NZ", "Maori"),
        new("mr", "mr", "mar", "Marathi", "मराठी", "mr-IN", "IN"),
        new("ne", "ne", "nep", "Nepali", "नेपाली", "ne-NP", "NP"),
        new("nb", "nb", "nor", "Norwegian", "Norsk bokmål", "nb-NO", "NO", "Bokmål", "Norwegian Bokmål"),
        new("or", "or", "ori", "Odia", "ଓଡ଼ିଆ", "or-IN", "IN", "Oriya"),
        new("fa", "fa", "fas", "Persian", "فارسی", "fa-IR", "IR", "Farsi"),
        new("pl", "pl", "pol", "Polish", "Polski", "pl-PL", "PL"),
        new("pt", "pt", "por", "Portuguese (Brazil)", "Português (Brasil)", "pt-BR", "BR", "Brazilian Portuguese", "Portuguese"),
        new("pa", "pa", "pan", "Punjabi", "ਪੰਜਾਬੀ", "pa-IN", "IN"),
        new("ro", "ro", "ron", "Romanian", "Română", "ro-RO", "RO"),
        new("ru", "ru", "rus", "Russian", "Русский", "ru-RU", "RU"),
        new("sr-Latn", "sr-Latn", "srp", "Serbian (Latin)", "Srpski (latinica)", "sr-Latn-RS", "RS", "sr", "Serbian"),
        new("sk", "sk", "slk", "Slovak", "Slovenčina", "sk-SK", "SK"),
        new("sl", "sl", "slv", "Slovenian", "Slovenščina", "sl-SI", "SI"),
        new("es", "es", "spa", "Spanish", "Español", "es-ES", "ES"),
        new("sw", "sw", "swa", "Swahili", "Kiswahili", "sw-TZ", "TZ"),
        new("sv", "sv", "swe", "Swedish", "Svenska", "sv-SE", "SE"),
        new("ta", "ta", "tam", "Tamil", "தமிழ்", "ta-IN", "IN"),
        new("te", "te", "tel", "Telugu", "తెలుగు", "te-IN", "IN"),
        new("th", "th", "tha", "Thai", "ไทย", "th-TH", "TH"),
        new("tr", "tr", "tur", "Turkish", "Türkçe", "tr-TR", "TR"),
        new("uk", "uk", "ukr", "Ukrainian", "Українська", "uk-UA", "UA"),
        new("uz", "uz", "uzb", "Uzbek", "Oʻzbekcha", "uz-UZ", "UZ"),
        new("vi", "vi", "vie", "Vietnamese", "Tiếng Việt", "vi-VN", "VN"),
        new("cy", "cy", "cym", "Welsh", "Cymraeg", "cy-GB", "GB"),
    ];

    public static IReadOnlyList<QuizLanguage> All { get; } = Array.AsReadOnly(Languages);

    public static int MaximumCodeLength { get; } = Languages.Max(language => language.Code.Length);

    public const int StorageCodeMaximumLength = 8;

    public static string SelectedLanguageCheckConstraintSql { get; } =
        $"[SelectedQuizLanguageCode] IS NULL OR [SelectedQuizLanguageCode] IN ({string.Join(", ", Languages.Select(language => $"'{language.Code}'"))})";

    public static QuizLanguage? Find(string? value)
    {
        var candidate = value?.Trim();
        if (string.IsNullOrEmpty(candidate))
        {
            return null;
        }

        return Languages.FirstOrDefault(language =>
            string.Equals(language.Code, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language.TranslatorCode, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language.ScribeCode, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language.Name, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(language.NativeName, candidate, StringComparison.OrdinalIgnoreCase)
            || language.Aliases.Contains(candidate, StringComparer.OrdinalIgnoreCase));
    }

    public static string NormalizeForSearch(string value) => string.Concat(
        value.Normalize(NormalizationForm.FormD)
            .Where(character => CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark))
        .ToLowerInvariant();
}

public interface IQuizLanguagePreferenceService
{
    Task<QuizLanguage?> GetSelectedAsync(string userId, CancellationToken cancellationToken = default);
    Task<QuizLanguage> SetSelectedAsync(string userId, string language, CancellationToken cancellationToken = default);
    Task ClearAsync(string userId, CancellationToken cancellationToken = default);
}

public sealed class QuizLanguagePreferenceService : IQuizLanguagePreferenceService
{
    private readonly GlosifyContext _context;

    public QuizLanguagePreferenceService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<QuizLanguage?> GetSelectedAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var code = await _context.Users
            .AsNoTracking()
            .Where(user => user.Id == userId)
            .Select(user => user.SelectedQuizLanguageCode)
            .SingleOrDefaultAsync(cancellationToken);
        return QuizLanguageCatalog.Find(code);
    }

    public async Task<QuizLanguage> SetSelectedAsync(
        string userId,
        string language,
        CancellationToken cancellationToken = default)
    {
        var selected = QuizLanguageCatalog.Find(language)
            ?? throw new ArgumentException("Choose a supported quiz language.", nameof(language));
        var user = await _context.Users.SingleAsync(user => user.Id == userId, cancellationToken);
        user.SelectedQuizLanguageCode = selected.Code;
        await _context.SaveChangesAsync(cancellationToken);
        return selected;
    }

    public async Task ClearAsync(string userId, CancellationToken cancellationToken = default)
    {
        var user = await _context.Users.SingleAsync(user => user.Id == userId, cancellationToken);
        user.SelectedQuizLanguageCode = null;
        await _context.SaveChangesAsync(cancellationToken);
    }
}
