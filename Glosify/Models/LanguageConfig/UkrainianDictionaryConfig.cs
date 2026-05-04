namespace Glosify.Models.LanguageConfig
{
    public sealed class UkrainianDictionaryConfig : LanguageDictionaryConfigBase
    {
        private static readonly System.Text.RegularExpressions.Regex RomanizationTail =
            new(@"\s*\([^)]*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        public override string LangCode => "uk";

        public override IReadOnlyList<string> Aliases => new[] { "uk", "ukrainian", "ukrainisch", "українськ" };

        // Older imported Ukrainian variants may include a romanization tail. Gemini output should not,
        // but keeping the cleanup lets existing cached rows render cleanly.
        public override string CleanForm(string form)
        {
            if (string.IsNullOrEmpty(form))
            {
                return form;
            }
            var stripped = RomanizationTail.Replace(form, string.Empty);
            return stripped.Length == 0 ? form : stripped;
        }

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
                    new("я", ["non-past", "first-person", "singular"]),
                    new("ти", ["non-past", "second-person", "singular"]),
                    new("він/вона", ["non-past", "third-person", "singular"]),
                    new("ми", ["non-past", "first-person", "plural"]),
                    new("ви", ["non-past", "second-person", "plural"]),
                    new("вони", ["non-past", "third-person", "plural"]),
                ]),
                new SlotGroup("Past Tense", [
                    new("masculine", ["past", "masculine", "singular"]),
                    new("feminine", ["past", "feminine", "singular"]),
                    new("neuter", ["past", "neuter", "singular"]),
                    new("plural", ["past", "plural"]),
                ]),
                new SlotGroup("Imperative", [
                    new("ти", ["imperative", "second-person", "singular"]),
                    new("ми", ["imperative", "first-person", "plural"]),
                    new("ви", ["imperative", "second-person", "plural"]),
                ])),

            "Adjective" => WordClassConfig.FromSlots("Adjective Forms", "palette",
                new SlotGroup("Nominative Singular", [
                    new("masculine", ["nominative", "masculine", "singular"]),
                    new("feminine", ["nominative", "feminine", "singular"]),
                    new("neuter", ["nominative", "neuter", "singular"]),
                ]),
                new SlotGroup("Nominative Plural", [
                    new("plural", ["nominative", "plural"]),
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
