using System.Collections.Frozen;

namespace Glosify.Services.Speaking;

public static class SpeakingAvatarCatalog
{
    private static readonly SpeakingAvatarDefinition[] Definitions =
    [
        new(
            SpeakingAvatarId.Bartender,
            "bartender",
            "The bartender",
            "Bar Pod Białym Orłem",
            "pl-PL-MarekNeural",
            "Cześć! Co podać? Lane czy coś mocniejszego?",
            "Hey! What can I get you? Draft beer or something stronger?",
            "0%",
            "0%"),
        new(
            SpeakingAvatarId.Kasia,
            "kasia",
            "Kasia",
            "Nocna Sowa",
            "pl-PL-ZofiaNeural",
            "Cześć! Często tu bywasz? Ja jestem tu pierwszy raz.",
            "Hi! Do you come here often? It’s my first time here.",
            "0%",
            "0%"),
        new(
            SpeakingAvatarId.Mietek,
            "mietek",
            "Pan Mietek",
            "Osiedle",
            "pl-PL-MarekNeural",
            "O, szanowny pan! Masz może pięć złotych? Oddam we wtorek, słowo.",
            "Oh, good sir! Got maybe five złoty? I’ll pay you back on Tuesday, my word.",
            "-12%",
            "-8%"),
    ];

    private static readonly FrozenDictionary<SpeakingAvatarId, SpeakingAvatarDefinition> ById =
        Definitions.ToFrozenDictionary(definition => definition.Id);

    private static readonly FrozenDictionary<string, SpeakingAvatarDefinition> BySlug =
        Definitions.ToFrozenDictionary(definition => definition.Slug, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<SpeakingAvatarDefinition> All => Definitions;

    public static SpeakingAvatarDefinition Get(SpeakingAvatarId id) => ById[id];

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

    public static bool TryParseCefr(string? value, out CefrLevel level) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out level)
        && Enum.IsDefined(level);

    public static bool TryParseInputMode(string? value, out SpeakingInputMode mode) =>
        Enum.TryParse(value?.Trim(), ignoreCase: true, out mode)
        && Enum.IsDefined(mode);

    public static SpeakingPageViewModel CreatePageViewModel() =>
        new(
            Definitions.Select(definition => new SpeakingPageAvatar(
                definition.Slug,
                definition.Name,
                definition.Scenario,
                definition.Voice,
                definition.OpeningPolish,
                definition.OpeningEnglish,
                definition.SsmlRate,
                definition.SsmlPitch)).ToArray(),
            "bartender",
            CefrLevel.A2.ToString());
}
