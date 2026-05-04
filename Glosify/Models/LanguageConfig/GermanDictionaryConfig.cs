namespace Glosify.Models.LanguageConfig
{
    public sealed class GermanDictionaryConfig : LanguageDictionaryConfigBase
    {
        public override string LangCode => "de";

        public override IReadOnlyList<string> Aliases => new[] { "de", "german", "deutsch" };

        public override WordClassConfig? GetWordClass(string pos) => pos switch
        {
            "Noun" => WordClassConfig.FromSlots("Noun Cases", "title",
                new SlotGroup("Singular", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("dative", ["dative", "singular"]),
                    new("accusative", ["accusative", "singular"]),
                ]),
                new SlotGroup("Plural", [
                    new("nominative", ["nominative", "plural"]),
                    new("genitive", ["genitive", "plural"]),
                    new("dative", ["dative", "plural"]),
                    new("accusative", ["accusative", "plural"]),
                ])),

            "Verb" => WordClassConfig.FromSlots("Verb Forms", "directions_run",
                new SlotGroup("Principal Forms", [
                    new("infinitive", ["infinitive"]),
                    new("past participle", ["past", "participle"]),
                    new("present participle", ["present", "participle"]),
                ]),
                new SlotGroup("Present Indicative", [
                    new("ich", ["first-person", "singular", "present", "indicative"]),
                    new("du", ["second-person", "singular", "present", "indicative"]),
                    new("er/sie/es", ["third-person", "singular", "present", "indicative"]),
                    new("wir", ["first-person", "plural", "present", "indicative"]),
                    new("ihr", ["second-person", "plural", "present", "indicative"]),
                    new("sie", ["third-person", "plural", "present", "indicative"]),
                ]),
                new SlotGroup("Simple Past", [
                    new("ich", ["first-person", "singular", "past", "indicative"]),
                    new("du", ["second-person", "singular", "past", "indicative"]),
                    new("er/sie/es", ["third-person", "singular", "past", "indicative"]),
                    new("wir", ["first-person", "plural", "past", "indicative"]),
                    new("ihr", ["second-person", "plural", "past", "indicative"]),
                    new("sie", ["third-person", "plural", "past", "indicative"]),
                ]),
                new SlotGroup("Imperative", [
                    new("du", ["imperative", "second-person", "singular"]),
                    new("ihr", ["imperative", "second-person", "plural"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective Degree", "palette",
                new SlotGroup("Degree", [
                    new("positive", ["positive"]),
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Pronoun" => WordClassConfig.FromSlots("Pronoun Cases", "person",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                ])),

            "Article" => WordClassConfig.FromSlots("Article Declension", "view_column",
                new SlotGroup("Masculine", [
                    new("nominative", ["nominative", "masculine", "singular"]),
                    new("genitive", ["genitive", "masculine", "singular"]),
                    new("dative", ["dative", "masculine", "singular"]),
                    new("accusative", ["accusative", "masculine", "singular"]),
                ]),
                new SlotGroup("Feminine", [
                    new("nominative", ["nominative", "feminine", "singular"]),
                    new("genitive", ["genitive", "feminine", "singular"]),
                    new("dative", ["dative", "feminine", "singular"]),
                    new("accusative", ["accusative", "feminine", "singular"]),
                ]),
                new SlotGroup("Neuter", [
                    new("nominative", ["nominative", "neuter", "singular"]),
                    new("genitive", ["genitive", "neuter", "singular"]),
                    new("dative", ["dative", "neuter", "singular"]),
                    new("accusative", ["accusative", "neuter", "singular"]),
                ]),
                new SlotGroup("Plural", [
                    new("nominative", ["nominative", "plural"]),
                    new("genitive", ["genitive", "plural"]),
                    new("dative", ["dative", "plural"]),
                    new("accusative", ["accusative", "plural"]),
                ])),

            "Numeral" => WordClassConfig.FromSlots("Numeral Cases", "pin",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                ])),

            "Adverb" => WordClassConfig.FromSlots("Adverb Comparison", "schedule",
                new SlotGroup("Comparison", [
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Preposition" => WordClassConfig.FromFacts("Preposition", "conversion_path",
                new("Governs Case", ["governs_case", "case"]),
                new("Meaning", ["relationship", "meaning_type"])),
            "Conjunction" => WordClassConfig.FromFacts("Conjunction", "lan",
                new FactDefinition("Type", ["type", "clause_type"])),
            "Interjection" => WordClassConfig.FromFacts("Interjection", "campaign",
                new("Tone", ["tone", "emotion"]),
                new("Register", ["register", "style"])),

            _ => null
        };
    }
}
