namespace Glosify.Models.LanguageConfig
{
    public sealed class PolishDictionaryConfig : LanguageDictionaryConfigBase
    {
        public override string LangCode => "pl";

        public override IReadOnlyList<string> Aliases => new[] { "pl", "polish", "polski", "polnisch" };

        public override WordClassConfig? GetWordClass(string pos) => pos switch
        {
            "Noun" => WordClassConfig.FromSlots("Noun Cases", "title",
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

            "Verb" => WordClassConfig.FromSlots("Verb Forms", "directions_run",
                new SlotGroup("Principal Forms", [
                    new("infinitive", ["infinitive"]),
                    new("imperfective", ["imperfective"]),
                    new("perfective", ["perfective"]),
                ]),
                new SlotGroup("Present / Future", [
                    new("first-person singular", ["non-past", "first-person", "singular"]),
                    new("second-person singular", ["non-past", "second-person", "singular"]),
                    new("third-person singular", ["non-past", "third-person", "singular"]),
                    new("first-person plural", ["non-past", "first-person", "plural"]),
                    new("second-person plural", ["non-past", "second-person", "plural"]),
                    new("third-person plural", ["non-past", "third-person", "plural"]),
                ]),
                new SlotGroup("Past Singular", [
                    new("1st masculine", ["past", "first-person", "masculine", "singular"]),
                    new("1st feminine", ["past", "first-person", "feminine", "singular"]),
                    new("2nd masculine", ["past", "second-person", "masculine", "singular"]),
                    new("2nd feminine", ["past", "second-person", "feminine", "singular"]),
                    new("3rd masculine", ["past", "third-person", "masculine", "singular"]),
                    new("3rd feminine", ["past", "third-person", "feminine", "singular"]),
                    new("3rd neuter", ["past", "third-person", "neuter", "singular"]),
                ]),
                new SlotGroup("Past Plural", [
                    new("1st masculine personal", ["past", "first-person", "plural", "masculine-personal"]),
                    new("1st non-masculine personal", ["past", "first-person", "plural", "non-masculine-personal"]),
                    new("2nd masculine personal", ["past", "second-person", "plural", "masculine-personal"]),
                    new("2nd non-masculine personal", ["past", "second-person", "plural", "non-masculine-personal"]),
                    new("3rd masculine personal", ["past", "third-person", "plural", "masculine-personal"]),
                    new("3rd non-masculine personal", ["past", "third-person", "plural", "non-masculine-personal"]),
                ]),
                new SlotGroup("Imperative", [
                    new("second-person singular", ["imperative", "second-person", "singular"]),
                    new("second-person plural", ["imperative", "second-person", "plural"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective Forms", "palette",
                new SlotGroup("Nominative Singular", [
                    new("masculine", ["nominative", "masculine", "singular"]),
                    new("feminine", ["nominative", "feminine", "singular"]),
                    new("neuter", ["nominative", "neuter", "singular"]),
                ]),
                new SlotGroup("Nominative Plural", [
                    new("masculine personal", ["nominative", "plural", "masculine-personal"]),
                    new("non-masculine personal", ["nominative", "plural", "non-masculine-personal"]),
                ]),
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
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
                ])),

            "Numeral" => WordClassConfig.FromSlots("Numeral Cases", "pin",
                new SlotGroup("Cases", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
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
            "Article" => null,
            "Interjection" => WordClassConfig.FromFacts("Interjection", "campaign",
                new("Tone", ["tone", "emotion"]),
                new("Register", ["register", "style"])),

            _ => null
        };
    }
}
