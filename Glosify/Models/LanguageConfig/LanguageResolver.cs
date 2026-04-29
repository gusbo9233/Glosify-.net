namespace Glosify.Models.LanguageConfig
{
    public static class LanguageResolver
    {
        public static string? ResolveLangCode(string? language)
        {
            if (string.IsNullOrWhiteSpace(language))
                return null;

            if (language.Equals("de", StringComparison.OrdinalIgnoreCase)
                || language.Contains("german", StringComparison.OrdinalIgnoreCase)
                || language.Contains("deutsch", StringComparison.OrdinalIgnoreCase))
                return "de";

            if (language.Equals("et", StringComparison.OrdinalIgnoreCase)
                || language.Contains("estonian", StringComparison.OrdinalIgnoreCase)
                || language.Contains("eesti", StringComparison.OrdinalIgnoreCase))
                return "et";

            if (language.Equals("uk", StringComparison.OrdinalIgnoreCase)
                || language.Contains("ukrainian", StringComparison.OrdinalIgnoreCase)
                || language.Contains("ukrainisch", StringComparison.OrdinalIgnoreCase)
                || language.Contains("українськ", StringComparison.OrdinalIgnoreCase))
                return "uk";

            if (language.Equals("pl", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polish", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polski", StringComparison.OrdinalIgnoreCase)
                || language.Contains("polnisch", StringComparison.OrdinalIgnoreCase))
                return "pl";

            return null;
        }

        public static string NormalizePartOfSpeech(string? partOfSpeech)
        {
            if (string.IsNullOrWhiteSpace(partOfSpeech))
                return string.Empty;

            return partOfSpeech.ToLowerInvariant() switch
            {
                "noun" or "nomen" or "hauptwort" or "substantiv" or "nimisõna" or "nimisona" or "іменник" or "rzeczownik" => "Noun",
                "verb" or "verben" or "zeitwort" or "tegusõna" or "tegusona" or "дієслово" or "czasownik" => "Verb",
                "article" or "artikel" or "geschlechtswort" or "artikkel" or "артикль" or "przedimek" or "rodzajnik" => "Article",
                "adjective" or "adjektiv" or "eigenschaftswort" or "omadussõna" or "omadussona" or "прикметник" or "przymiotnik" => "Adjective",
                "pronoun" or "pronomen" or "fuerwort" or "fürwort" or "asesõna" or "asesona" or "займенник" or "zaimek" => "Pronoun",
                "adverb" or "adverbien" or "umstandswort" or "määrsõna" or "maarsona" or "прислівник" or "przysłówek" or "przyslowek" => "Adverb",
                "preposition" or "präposition" or "praeposition" or "verhältniswort" or "verhaeltniswort"
                    or "postposition" or "adposition" or "kaassõna" or "kaassona" or "прийменник" or "przyimek" => "Preposition",
                "conjunction" or "konjunktion" or "bindewort" or "sidesõna" or "sidesona" or "сполучник" or "spójnik" or "spojnik" => "Conjunction",
                "numeral" or "numerale" or "zahlwort" or "arvsõna" or "arvsona" or "числівник" or "liczebnik" => "Numeral",
                "interjection" or "interjektion" or "einwurf" or "hüüdsõna" or "huudsona" or "вигук" or "wykrzyknik" => "Interjection",
                _ => string.Empty
            };
        }
    }
}
