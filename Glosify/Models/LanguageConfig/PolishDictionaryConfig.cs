namespace Glosify.Models.LanguageConfig
{
    public sealed class PolishDictionaryConfig : ILanguageDictionaryConfig
    {
        public string LangCode => "pl";

        public WordClassConfig? GetWordClass(string pos) => pos switch
        {
            "Noun" => WordClassConfig.FromSlots("Noun · Rzeczownik", "title",
                new SlotGroup("Singular", [
                    new("nominative", ["nominative", "singular"]),
                    new("genitive", ["genitive", "singular"]),
                    new("dative", ["dative", "singular"]),
                    new("accusative", ["accusative", "singular"]),
                    new("instrumental", ["instrumental", "singular"]),
                    new("locative", ["locative", "singular"]),
                    new("vocative", ["vocative", "singular"]),
                ]),
                new SlotGroup("Plural", [
                    new("nominative", ["nominative", "plural"]),
                    new("genitive", ["genitive", "plural"]),
                    new("dative", ["dative", "plural"]),
                    new("accusative", ["accusative", "plural"]),
                    new("instrumental", ["instrumental", "plural"]),
                    new("locative", ["locative", "plural"]),
                    new("vocative", ["vocative", "plural"]),
                ])),

            "Verb" => WordClassConfig.FromSlots("Verb · Czasownik", "directions_run",
                new SlotGroup("Infinitive & Participles", [
                    new("infinitive", ["infinitive"]),
                    new("active adjectival", ["participle", "active", "adjectival"]),
                    new("passive adjectival", ["participle", "passive", "adjectival"]),
                    new("contemporary", ["participle", "contemporary"]),
                    new("verbal noun", ["noun-from-verb"]),
                ]),
                new SlotGroup("Present", [
                    new("singular", ["singular", "present"]),
                    new("plural", ["plural", "present"]),
                    new("third-person singular", ["third-person", "singular", "present"]),
                    new("third-person plural", ["third-person", "plural", "present"]),
                    new("impersonal", ["impersonal", "present"]),
                ]),
                new SlotGroup("Past Tense (singular)", [
                    new("masculine", ["past", "masculine", "singular"]),
                    new("feminine", ["past", "feminine", "singular"]),
                    new("neuter", ["past", "neuter", "singular"]),
                ]),
                new SlotGroup("Past Tense (plural)", [
                    new("masculine personal", ["past", "plural", "virile"]),
                    new("non-masculine personal", ["past", "plural", "nonvirile"]),
                ]),
                new SlotGroup("Future", [
                    new("singular masculine", ["future", "masculine", "singular"]),
                    new("singular feminine", ["future", "feminine", "singular"]),
                    new("singular neuter", ["future", "neuter", "singular"]),
                    new("plural virile", ["future", "plural", "virile"]),
                    new("plural nonvirile", ["future", "plural", "nonvirile"]),
                    new("impersonal", ["future", "impersonal"]),
                ]),
                new SlotGroup("Imperative", [
                    new("singular", ["imperative", "singular"]),
                    new("plural", ["imperative", "plural"]),
                    new("third-person singular", ["imperative", "third-person", "singular"]),
                    new("third-person plural", ["imperative", "third-person", "plural"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective · Przymiotnik", "palette",
                new SlotGroup("Singular", [
                    new("masculine", ["masculine", "singular"]),
                    new("feminine", ["feminine", "singular"]),
                    new("neuter", ["neuter", "singular"]),
                ]),
                new SlotGroup("Plural", [
                    new("masculine personal", ["plural", "virile"]),
                    new("non-masculine personal", ["plural"]),
                ]),
                new SlotGroup("Degree", [
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Pronoun" => WordClassConfig.FromSlots("Pronoun · Zaimek", "person",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
                ])),

            "Numeral" => WordClassConfig.FromSlots("Numeral · Liczebnik", "pin",
                new SlotGroup("Cases", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
                ])),

            "Adverb" => WordClassConfig.FromSlots("Adverb · Przysłówek", "schedule",
                new SlotGroup("Comparison", [
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Preposition" => null,
            "Conjunction" => null,
            "Article" => null,
            "Interjection" => null,

            _ => null
        };
    }
}
