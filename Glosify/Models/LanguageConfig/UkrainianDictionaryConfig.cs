namespace Glosify.Models.LanguageConfig
{
    public sealed class UkrainianDictionaryConfig : LanguageDictionaryConfigBase
    {
        private static readonly System.Text.RegularExpressions.Regex RomanizationTail =
            new(@"\s*\([^)]*\)\s*$", System.Text.RegularExpressions.RegexOptions.Compiled);

        public override string LangCode => "uk";

        public override IReadOnlyList<string> Aliases => new[] { "uk", "ukrainian", "ukrainisch", "українськ" };

        public override bool BundlesPronounParadigm => true;

        // Kaikki Ukrainian variants often store the form with a stress-romanization tail baked in,
        // e.g. "мéне (mené, méne*)". Strip that for display so slot cells aren't littered with parens.
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
            "Noun" => WordClassConfig.FromSlots("Noun · Іменник", "title",
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

            "Verb" => WordClassConfig.FromSlots("Verb · Дієслово", "directions_run",
                new SlotGroup("Infinitive & Participles", [
                    new("infinitive", ["infinitive"]),
                    new("present active participle", ["participle", "present", "active"]),
                    new("past active participle", ["participle", "past", "active"]),
                    new("past passive participle", ["participle", "past", "passive"]),
                    new("verbal adverb", ["adverbial"]),
                ]),
                new SlotGroup("Present / Future", [
                    new("я", ["first-person", "singular", "present"]),
                    new("ти", ["second-person", "singular", "present"]),
                    new("він/вона", ["third-person", "singular", "present"]),
                    new("ми", ["first-person", "plural", "present"]),
                    new("ви", ["second-person", "plural", "present"]),
                    new("вони", ["third-person", "plural", "present"]),
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

            "Adjective" => WordClassConfig.FromSlots("Adjective · Прикметник", "palette",
                new SlotGroup("Singular", [
                    new("masculine", ["masculine", "singular", "nominative"]),
                    new("feminine", ["feminine", "singular", "nominative"]),
                    new("neuter", ["neuter", "singular", "nominative"]),
                ]),
                new SlotGroup("Plural", [
                    new("nominative", ["plural", "nominative"]),
                ]),
                new SlotGroup("Degree", [
                    new("comparative", ["comparative"]),
                    new("superlative", ["superlative"]),
                ])),

            "Pronoun" => WordClassConfig.FromSlots("Pronoun · Займенник", "person",
                new SlotGroup("Case Forms", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
                ])),

            "Numeral" => WordClassConfig.FromSlots("Numeral · Числівник", "pin",
                new SlotGroup("Cases", [
                    new("nominative", ["nominative"]),
                    new("genitive", ["genitive"]),
                    new("dative", ["dative"]),
                    new("accusative", ["accusative"]),
                    new("instrumental", ["instrumental"]),
                    new("locative", ["locative"]),
                ])),

            "Adverb" => WordClassConfig.FromSlots("Adverb · Прислівник", "schedule",
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
