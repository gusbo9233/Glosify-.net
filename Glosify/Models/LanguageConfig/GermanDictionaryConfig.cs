namespace Glosify.Models.LanguageConfig
{
    public sealed class GermanDictionaryConfig : LanguageDictionaryConfigBase
    {
        public override string LangCode => "de";

        public override IReadOnlyList<string> Aliases => new[] { "de", "german", "deutsch" };

        public override WordClassConfig? GetWordClass(string pos) => pos switch
        {
            "Noun" => WordClassConfig.FromSlots("Noun · Nomen", "title",
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

            "Verb" => WordClassConfig.FromSlots("Verb · Verb", "directions_run",
                new SlotGroup("Principal Forms", [
                    new("infinitive", ["infinitive"]),
                    new("past participle", ["past", "participle"]),
                    new("present participle", ["present", "participle"]),
                ]),
                new SlotGroup("Present", [
                    new("ich", ["first-person", "singular", "present", "indicative"]),
                    new("du", ["second-person", "singular", "present", "indicative"]),
                    new("er/sie/es", ["third-person", "singular", "present", "indicative"]),
                    new("wir", ["first-person", "plural", "present", "indicative"]),
                    new("ihr", ["second-person", "plural", "present", "indicative"]),
                    new("sie", ["third-person", "plural", "present", "indicative"]),
                ]),
                new SlotGroup("Preterite", [
                    new("ich", ["first-person", "singular", "preterite"]),
                    new("du", ["second-person", "singular", "preterite"]),
                    new("er/sie/es", ["third-person", "singular", "preterite"]),
                    new("wir", ["first-person", "plural", "preterite"]),
                    new("ihr", ["second-person", "plural", "preterite"]),
                    new("sie", ["third-person", "plural", "preterite"]),
                ]),
                new SlotGroup("Subjunctive II", [
                    new("ich", ["first-person", "singular", "subjunctive-ii"]),
                    new("du", ["second-person", "singular", "subjunctive-ii"]),
                    new("er/sie/es", ["third-person", "singular", "subjunctive-ii"]),
                    new("wir", ["first-person", "plural", "subjunctive-ii"]),
                ]),
                new SlotGroup("Imperative", [
                    new("du", ["imperative", "second-person", "singular"]),
                    new("ihr", ["imperative", "second-person", "plural"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective · Adjektiv", "palette",
                new SlotGroup("Degree", [
                    new("positive", ["positive"]),
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Pronoun" => WordClassConfig.FromSlots("Pronoun · Pronomen", "person",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                ])),

            "Article" => null,

            "Numeral" => WordClassConfig.FromSlots("Numeral · Numerale", "pin",
                new SlotGroup("Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                ])),

            "Adverb" => WordClassConfig.FromSlots("Adverb · Adverb", "schedule",
                new SlotGroup("Comparison", [
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Preposition" => null,
            "Conjunction" => null,
            "Interjection" => null,

            _ => null
        };
    }
}
