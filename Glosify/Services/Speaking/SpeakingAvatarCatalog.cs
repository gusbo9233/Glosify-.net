using System.Collections.Frozen;

namespace Glosify.Services.Speaking;

public static class SpeakingAvatarCatalog
{
    private static readonly SpeakingAvatarDefinition[] Definitions =
    [
        new(
            SpeakingAvatarId.Bartender,
            "bartender",
            "Polish",
            "pl-PL",
            "The bartender",
            "Bar Pod Białym Orłem",
            "pl-PL-MarekNeural",
            "Cześć! Co podać? Lane czy coś mocniejszego?",
            "Hey! What can I get you? Draft beer or something stronger?",
            "0%",
            "0%",
            "custom",
            "man",
            "POD BIAŁYM ORŁEM",
            "#bf8150"),
        new(
            SpeakingAvatarId.Kasia,
            "kasia",
            "Polish",
            "pl-PL",
            "Kasia",
            "Nocna Sowa",
            "pl-PL-ZofiaNeural",
            "Cześć! Często tu bywasz? Ja jestem tu pierwszy raz.",
            "Hi! Do you come here often? It’s my first time here.",
            "0%",
            "0%",
            "custom",
            "woman",
            "NOCNA SOWA",
            "#d86d91"),
        new(
            SpeakingAvatarId.Mietek,
            "mietek",
            "Polish",
            "pl-PL",
            "Pan Mietek",
            "Osiedle",
            "pl-PL-MarekNeural",
            "O, szanowny pan! Masz może pięć złotych? Oddam we wtorek, słowo.",
            "Oh, good sir! Got maybe five złoty? I’ll pay you back on Tuesday, my word.",
            "-12%",
            "-8%",
            "custom",
            "older-man",
            "OSIEDLE",
            "#778089"),
        new(
            SpeakingAvatarId.Maarja,
            "maarja",
            "Estonian",
            "et-EE",
            "Maarja",
            "Vanalinna kohvik",
            "et-EE-AnuNeural",
            "Tere! Kas soovid kohvi või räägime lihtsalt natuke?",
            "Hi! Would you like coffee, or shall we just chat for a bit?",
            "0%",
            "0%",
            "cafe",
            "woman",
            "KOHVIK",
            "#4ba6c8"),
        new(
            SpeakingAvatarId.Karl,
            "karl",
            "Estonian",
            "et-EE",
            "Karl",
            "Balti Jaama turg",
            "et-EE-KertNeural",
            "Tere! Kas otsid midagi head või harjutame niisama eesti keelt?",
            "Hi! Are you looking for something good, or shall we just practise Estonian?",
            "0%",
            "0%",
            "market",
            "man",
            "BALTI JAAM",
            "#d08b43"),
        new(
            SpeakingAvatarId.Liis,
            "liis",
            "Estonian",
            "et-EE",
            "Liis",
            "Kadrioru park",
            "et-EE-AnuNeural",
            "Tere! Ilus päev jalutamiseks. Kuidas sul täna läheb?",
            "Hi! It’s a lovely day for a walk. How are you doing today?",
            "-4%",
            "2%",
            "park",
            "woman",
            "KADRIORG",
            "#6eaa73"),
        new(
            SpeakingAvatarId.Hanna,
            "hanna",
            "German",
            "de-DE",
            "Hanna",
            "Café Morgenrot",
            "de-DE-KatjaNeural",
            "Hallo! Möchtest du einen Kaffee, oder plaudern wir einfach ein bisschen?",
            "Hi! Would you like a coffee, or shall we just chat for a bit?",
            "0%",
            "0%",
            "cafe",
            "woman",
            "CAFÉ MORGENROT",
            "#c56d72"),
        new(
            SpeakingAvatarId.Jonas,
            "jonas",
            "German",
            "de-DE",
            "Jonas",
            "Bahnhofskiosk",
            "de-DE-ConradNeural",
            "Hi! Wartest du auf einen Zug, oder hast du Zeit für ein kurzes Gespräch?",
            "Hi! Are you waiting for a train, or do you have time for a quick chat?",
            "0%",
            "0%",
            "market",
            "man",
            "BAHNHOF",
            "#d39a42"),
        new(
            SpeakingAvatarId.FrauSchneider,
            "frau-schneider",
            "German",
            "de-DE",
            "Frau Schneider",
            "Nachbarschaftsgarten",
            "de-DE-KatjaNeural",
            "Guten Tag! Die Rosen blühen endlich. Wie gefällt dir der Garten?",
            "Good afternoon! The roses are finally blooming. How do you like the garden?",
            "-8%",
            "-3%",
            "park",
            "older-woman",
            "GARTEN",
            "#799b68"),
        new(
            SpeakingAvatarId.Oksana,
            "oksana",
            "Ukrainian",
            "uk-UA",
            "Оксана",
            "Кав’ярня «Ліхтар»",
            "uk-UA-PolinaNeural",
            "Привіт! Тобі каву чи просто трохи поговоримо?",
            "Hi! Would you like coffee, or shall we just talk for a bit?",
            "0%",
            "0%",
            "cafe",
            "woman",
            "КАВ’ЯРНЯ",
            "#4f91c7"),
        new(
            SpeakingAvatarId.Andriy,
            "andriy",
            "Ukrainian",
            "uk-UA",
            "Андрій",
            "Бессарабський ринок",
            "uk-UA-OstapNeural",
            "Вітаю! Шукаєш щось смачне чи просто практикуєш українську?",
            "Hello! Are you looking for something tasty, or just practising Ukrainian?",
            "0%",
            "0%",
            "market",
            "man",
            "РИНОК",
            "#d5a22e"),
        new(
            SpeakingAvatarId.PanMykola,
            "pan-mykola",
            "Ukrainian",
            "uk-UA",
            "Пан Микола",
            "Двір біля дому",
            "uk-UA-OstapNeural",
            "Добрий день! Сідай поруч. Як минув твій день?",
            "Good afternoon! Sit down beside me. How was your day?",
            "-10%",
            "-5%",
            "park",
            "older-man",
            "НАШ ДВІР",
            "#638c6b"),
    ];

    private static readonly FrozenDictionary<SpeakingAvatarId, SpeakingAvatarDefinition> ById =
        Definitions.ToFrozenDictionary(definition => definition.Id);

    private static readonly FrozenDictionary<string, SpeakingAvatarDefinition> BySlug =
        Definitions.ToFrozenDictionary(definition => definition.Slug, StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, SpeakingAvatarDefinition[]> ByLanguage =
        Definitions
            .GroupBy(definition => definition.Language, StringComparer.OrdinalIgnoreCase)
            .ToFrozenDictionary(
                group => group.Key,
                group => group.ToArray(),
                StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SpeakingAvatarDefinition> All => Definitions;

    public static SpeakingAvatarDefinition Get(SpeakingAvatarId id) => ById[id];

    public static IReadOnlyList<SpeakingAvatarDefinition> ForLanguage(string? language)
    {
        if (!string.IsNullOrWhiteSpace(language)
            && ByLanguage.TryGetValue(language.Trim(), out var definitions))
        {
            return definitions;
        }

        return [];
    }

    public static bool TryParse(string? value, out SpeakingAvatarDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(value)
            && BySlug.TryGetValue(value.Trim(), out var parsed))
        {
            definition = parsed;
            return true;
        }

        definition = Definitions[0];
        return false;
    }

    public static bool TryParseForLanguage(
        string? value,
        string? language,
        out SpeakingAvatarDefinition definition)
    {
        if (TryParse(value, out var parsed)
            && !string.IsNullOrWhiteSpace(language)
            && string.Equals(parsed.Language, language.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            definition = parsed;
            return true;
        }

        definition = Definitions[0];
        return false;
    }

    public static bool TryParseCefr(string? value, out CefrLevel level) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out level)
        && Enum.IsDefined(level);

    public static bool TryParseInputMode(string? value, out SpeakingInputMode mode) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out mode)
        && Enum.IsDefined(mode);

    public static SpeakingPageViewModel CreatePageViewModel(string language)
    {
        var definitions = ForLanguage(language);
        if (definitions.Count == 0)
        {
            throw new ArgumentException("Speaking avatars are not configured for this language.", nameof(language));
        }

        var first = definitions[0];
        return new SpeakingPageViewModel(
            definitions.Select(definition => new SpeakingPageAvatar(
                definition.Slug,
                definition.Language,
                definition.Locale,
                definition.LanguageCode,
                definition.Name,
                definition.Scenario,
                definition.Voice,
                definition.OpeningPolish,
                definition.OpeningEnglish,
                definition.SsmlRate,
                definition.SsmlPitch,
                definition.SceneTemplate,
                definition.PortraitStyle,
                definition.SceneSign,
                definition.AccentColor)).ToArray(),
            first.Language,
            first.Locale,
            first.LanguageCode,
            first.Slug,
            CefrLevel.A2.ToString());
    }
}
