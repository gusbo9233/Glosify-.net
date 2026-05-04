namespace Glosify.Models.LanguageConfig
{
    public sealed class EstonianDictionaryConfig : LanguageDictionaryConfigBase
    {
        public override string LangCode => "et";

        public override IReadOnlyList<string> Aliases => new[] { "et", "estonian", "eesti" };

        public override WordClassConfig? GetWordClass(string pos) => pos switch
        {
            "Noun" => WordClassConfig.FromSlots("Noun Cases", "title",
                new("Core Singular", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("partitive", ["partitive", "singular"]),
                ]),
                new("Core Plural", [
                    new("plural nominative", ["nominative", "plural"]),
                    new("plural genitive", ["genitive", "plural"]),
                    new("plural partitive", ["partitive", "plural"]),
                ]),
                new("Local Cases", [
                    new("illative", ["illative", "singular"]),
                    new("inessive", ["inessive", "singular"]),
                    new("elative", ["elative", "singular"]),
                    new("allative", ["allative", "singular"]),
                    new("adessive", ["adessive", "singular"]),
                    new("ablative", ["ablative", "singular"]),
                ]),
                new("Other Cases", [
                    new("translative", ["translative", "singular"]),
                    new("terminative", ["terminative", "singular"]),
                    new("essive", ["essive", "singular"]),
                    new("abessive", ["abessive", "singular"]),
                    new("comitative", ["comitative", "singular"]),
                ])),

            "Verb" => WordClassConfig.FromSlots("Verb Forms", "directions_run",
                new("Principal Forms", [
                    new("ma-infinitive", ["ma-infinitive"]),
                    new("da-infinitive", ["da-infinitive"]),
                    new("Present participle", ["participle", "present", "active"]),
                    new("Past participle", ["participle", "past", "active"]),
                    new("Impersonal participle", ["participle", "past", "passive"]),
                ]),
                new("Indicative Present", [
                    new("mina", ["indicative", "present", "first-person", "singular"]),
                    new("sina", ["indicative", "present", "second-person", "singular"]),
                    new("tema", ["indicative", "present", "third-person", "singular"]),
                    new("meie", ["indicative", "present", "first-person", "plural"]),
                    new("teie", ["indicative", "present", "second-person", "plural"]),
                    new("nemad", ["indicative", "present", "third-person", "plural"]),
                    new("negative", ["indicative", "present", "negative"]),
                ]),
                new("Indicative Past", [
                    new("mina", ["indicative", "past", "first-person", "singular"]),
                    new("sina", ["indicative", "past", "second-person", "singular"]),
                    new("tema", ["indicative", "past", "third-person", "singular"]),
                    new("meie", ["indicative", "past", "first-person", "plural"]),
                    new("teie", ["indicative", "past", "second-person", "plural"]),
                    new("nemad", ["indicative", "past", "third-person", "plural"]),
                    new("negative", ["indicative", "past", "negative"]),
                ]),
                new("Other Common Forms", [
                    new("conditional", ["conditional", "present", "third-person", "singular"]),
                    new("imperative 2sg", ["imperative", "present", "second-person", "singular"]),
                    new("imperative 2pl", ["imperative", "present", "second-person", "plural"]),
                    new("quotative", ["quotative", "present", "active"]),
                    new("impersonal present", ["impersonal", "indicative", "present"]),
                    new("impersonal past", ["impersonal", "indicative", "past"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective Forms", "palette",
                new("Degree", [
                    new("positive", ["positive"]),
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ]),
                new("Core Singular", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("partitive", ["partitive", "singular"]),
                ]),
                new("Core Plural", [
                    new("plural nominative", ["nominative", "plural"]),
                    new("plural genitive", ["genitive", "plural"]),
                    new("plural partitive", ["partitive", "plural"]),
                ])),

            "Pronoun" => WordClassConfig.FromSlots("Pronoun Cases", "person",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("partitive", ["partitive", "singular"]),
                    new("illative", ["illative", "singular"]),
                    new("inessive", ["inessive", "singular"]),
                    new("elative", ["elative", "singular"]),
                ])),

            "Numeral" => WordClassConfig.FromSlots("Numeral Cases", "pin",
                new SlotGroup("Inflection", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("partitive", ["partitive", "singular"]),
                ])),

            "Adverb" => WordClassConfig.FromFacts("Adverb", "schedule",
                new("Semantic Type", ["type", "semantic_type"]),
                new("Origin", ["origin", "derivation"])),

            "Preposition" => WordClassConfig.FromFacts("Adposition", "conversion_path",
                new("Position", ["position", "type"]),
                new("Governs Case", ["governs_case", "case"]),
                new("Relationship", ["relationship", "meaning_type"])),

            "Conjunction" => WordClassConfig.FromFacts("Conjunction", "lan",
                new("Type", ["type", "clause_type"]),
                new("Connects", ["connects", "scope"]),
                new("Punctuation", ["punctuation", "comma"])),

            "Interjection" => WordClassConfig.FromFacts("Interjection", "campaign",
                new("Tone", ["tone", "emotion"]),
                new("Register", ["register", "style"])),

            "Article" => WordClassConfig.FromFacts("Article", "view_column",
                new FactDefinition("Definiteness Cues", ["definiteness", "cues"])),

            _ => null
        };
    }
}
