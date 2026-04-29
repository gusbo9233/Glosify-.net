namespace Glosify.Models.LanguageConfig
{
    public interface ILanguageDictionaryConfig
    {
        string LangCode { get; }
        WordClassConfig? GetWordClass(string normalizedPartOfSpeech);
    }

    public sealed record FactDefinition(string Label, string[] PropertyKeys);

    public sealed record SlotDefinition(string Label, string[] Tags);

    public sealed record SlotGroup(string Heading, SlotDefinition[] Slots);

    public sealed record WordClassConfig(
        string Title,
        string Icon,
        FactDefinition[] Facts,
        SlotGroup[] SlotGroups,
        string[] VariantTagFilters
    )
    {
        public static WordClassConfig FromFacts(string title, string icon, params FactDefinition[] facts)
            => new(title, icon, facts, [], []);

        public static WordClassConfig FromSlots(string title, string icon, params SlotGroup[] groups)
            => new(title, icon, [], groups, []);
    }
}
