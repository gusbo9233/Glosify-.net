namespace Glosify.Models.LanguageConfig
{
    public interface ILanguageDictionaryConfig
    {
        string LangCode { get; }

        // Aliases used to recognize the language from a free-form string ("german", "deutsch", "de", …).
        // The 2-letter code matches by equality; longer aliases match by Contains.
        IReadOnlyList<string> Aliases { get; }

        // True only for legacy dictionary data whose pronoun entries bundle every personal pronoun's
        // paradigm into a single variants array. Gemini-generated details should leave this false.
        bool BundlesPronounParadigm { get; }

        WordClassConfig? GetWordClass(string normalizedPartOfSpeech);

        // Filter out kaikki "header" rows (et-decl-sõna, l-self, glossary, class markers,
        // forms tagged error-*) before they reach ranking or rendering.
        bool IsJunkVariant(WordDetailVariantViewModel variant);

        // Sanitize a form string before display — e.g. strip Ukrainian's romanization tail "(mené, méne*)".
        string CleanForm(string form);
    }

    public abstract class LanguageDictionaryConfigBase : ILanguageDictionaryConfig
    {
        public abstract string LangCode { get; }
        public virtual IReadOnlyList<string> Aliases => new[] { LangCode };
        public virtual bool BundlesPronounParadigm => false;

        public abstract WordClassConfig? GetWordClass(string normalizedPartOfSpeech);

        public virtual bool IsJunkVariant(WordDetailVariantViewModel variant)
        {
            if (variant.Tags.Any(tag => tag.StartsWith("error-", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
            var form = variant.Form;
            if (string.IsNullOrWhiteSpace(form) || form == "-" || form == "—")
            {
                return true;
            }
            // Cross-language kaikki header / class-marker pseudo-forms.
            if (form.Equals("glossary", StringComparison.OrdinalIgnoreCase)
                || form.Equals("l", StringComparison.OrdinalIgnoreCase)
                || form.Equals("l-self", StringComparison.OrdinalIgnoreCase)
                || form.Equals("strong", StringComparison.OrdinalIgnoreCase)
                || form.Equals("weak", StringComparison.OrdinalIgnoreCase)
                || form.Equals("mixed", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            // Template/class identifiers like "et-decl-sõna", "pl-decl-noun-masc-inani", "et-conj-nägema",
            // "no gradation", "g-ø gradation", "17u/sõna", "28h/nägema".
            if (form.Contains("-decl-", StringComparison.OrdinalIgnoreCase)
                || form.Contains("-conj-", StringComparison.OrdinalIgnoreCase)
                || form.EndsWith(" gradation", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(form, @"^\d+[a-z]?/"))
            {
                return true;
            }
            // Variants tagged purely as a "class" classifier are not real forms.
            if (variant.Tags.Count == 1 && variant.Tags[0].Equals("class", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
            return false;
        }

        public virtual string CleanForm(string form) => form;
    }

    public sealed record FactDefinition(string Label, string[] PropertyKeys);

    public sealed record SlotDefinition(string Label, string[] Tags);

    public sealed record SlotGroup(string Heading, SlotDefinition[] Slots);

    public sealed record WordClassConfig(
        string Title,
        string Icon,
        FactDefinition[] Facts,
        SlotGroup[] SlotGroups
    )
    {
        public static WordClassConfig FromFacts(string title, string icon, params FactDefinition[] facts)
            => new(title, icon, facts, []);

        public static WordClassConfig FromSlots(string title, string icon, params SlotGroup[] groups)
            => new(title, icon, [], groups);
    }
}
