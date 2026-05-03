using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using System.Globalization;
using System.Text;

namespace Glosify.Services;

public class DictionaryService : IDictionaryService
{
    private const int SingleLookupVariantRowCap = 20;

    private readonly GlosifyContext _context;
    private readonly IReadOnlyDictionary<string, ILanguageDictionaryConfig> _configsByCode;
    private readonly IReadOnlyList<ILanguageDictionaryConfig> _configs;

    public DictionaryService(GlosifyContext context, IEnumerable<ILanguageDictionaryConfig> languageConfigs)
    {
        _context = context;
        _configs = languageConfigs.ToList();
        _configsByCode = _configs.ToDictionary(c => c.LangCode, StringComparer.OrdinalIgnoreCase);
    }

    public Task<DictionaryEntry?> FindManualDictionaryMatchAsync(string language, string word)
        => FindBestDictionaryMatchAsync(language, word, selectedPartOfSpeech: null);

    public async Task<DictionaryEntry?> FindBestDictionaryMatchAsync(
        string language, string word, string? selectedPartOfSpeech)
    {
        var langCode = MatchSupportedLangCode(language);
        if (langCode == null)
        {
            return null;
        }
        var config = _configsByCode[langCode];

        var candidates = GetManualDictionaryCandidates(word);
        if (candidates.Count == 0)
        {
            return null;
        }

        var headwordEntries = await LoadHeadwordEntriesAsync(langCode, candidates);
        var headwordMatch = SelectBestHeadword(headwordEntries, candidates, selectedPartOfSpeech);

        // The variant query is an unindexable OPENJSON CROSS APPLY scan over the whole language —
        // skip it when we already have a real headword hit. Only fall back to variant resolution
        // when the user typed something that didn't match a headword, or when the headword we
        // found is itself an inflection-of-something stub.
        if (headwordMatch != null && !IsInflectionStub(headwordMatch))
        {
            return headwordMatch;
        }

        var variantEntries = await LoadVariantEntriesAsync(langCode, candidates, SingleLookupVariantRowCap);
        var variantMatch = SelectBestVariant(ParseVariants(variantEntries, config), candidates, selectedPartOfSpeech);
        return PreferVariantParent(headwordMatch, variantMatch);
    }

    public async Task<Dictionary<string, DictionaryEntry?>> BatchFindDictionaryMatchesAsync(
        string language, IReadOnlyList<string> lemmas)
    {
        var result = new Dictionary<string, DictionaryEntry?>(StringComparer.OrdinalIgnoreCase);
        var langCode = MatchSupportedLangCode(language);
        if (langCode == null || lemmas.Count == 0)
        {
            return result;
        }

        var lemmaToCandidates = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var lemma in lemmas)
        {
            if (lemma == null || result.ContainsKey(lemma))
            {
                continue;
            }
            result[lemma] = null;
            lemmaToCandidates[lemma] = GetManualDictionaryCandidates(lemma);
        }

        var allCandidates = lemmaToCandidates.Values
            .SelectMany(c => c)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (allCandidates.Count == 0)
        {
            return result;
        }

        var headwordEntries = await LoadHeadwordEntriesAsync(langCode, allCandidates);
        var headwordByWord = headwordEntries
            .GroupBy(entry => entry.Word, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => (IReadOnlyList<DictionaryEntry>)group.ToList(), StringComparer.OrdinalIgnoreCase);

        // Skip the OPENJSON variant query in batch: it's the slow part and AI-generated input
        // is already lemma-shaped, so headword matches carry ~all of the value. Single-lookup
        // path still does variant resolution for users typing inflected forms.
        foreach (var (lemma, candidates) in lemmaToCandidates)
        {
            var relevantHeadwords = candidates
                .Where(headwordByWord.ContainsKey)
                .SelectMany(c => headwordByWord[c]);

            result[lemma] = SelectBestHeadword(relevantHeadwords, candidates, selectedPartOfSpeech: null);
        }

        return result;
    }

    private async Task<List<DictionaryEntry>> LoadHeadwordEntriesAsync(string langCode, IReadOnlyList<string> candidates)
    {
        return await _context.DictionaryEntries
            .AsNoTracking()
            .Where(e => e.LangCode == langCode && candidates.Contains(e.Word))
            .ToListAsync();
    }

    private async Task<List<DictionaryEntry>> LoadVariantEntriesAsync(
        string langCode, IReadOnlyList<string> candidates, int rowCap)
    {
        if (candidates.Count == 0)
        {
            return new List<DictionaryEntry>();
        }

        var parameters = new List<object>
        {
            new SqlParameter("@langCode", langCode),
            new SqlParameter("@top", rowCap),
        };
        var inPlaceholders = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            var name = $"@c{i}";
            inPlaceholders[i] = name;
            parameters.Add(new SqlParameter(name, candidates[i]));
        }

        var sql = $"""
            SELECT TOP (@top) de.*
            FROM dbo.dictionary_entries de
            CROSS APPLY OPENJSON(de.variants) variant
            WHERE de.lang_code = @langCode
                AND JSON_VALUE(variant.value, '$.form') COLLATE Latin1_General_100_CI_AI IN ({string.Join(", ", inPlaceholders)})
            ORDER BY LEN(de.word)
            """;

        return await _context.DictionaryEntries
            .FromSqlRaw(sql, parameters.ToArray())
            .AsNoTracking()
            .ToListAsync();
    }

    private static List<(DictionaryEntry Entry, IReadOnlyList<WordDetailVariantViewModel> Variants)> ParseVariants(
        IEnumerable<DictionaryEntry> entries, ILanguageDictionaryConfig config)
    {
        return entries
            .Select(e => (
                Entry: e,
                Variants: (IReadOnlyList<WordDetailVariantViewModel>)WordDetailJsonReader.ReadVariants(e.Variants)
                    .Where(v => !config.IsJunkVariant(v))
                    .Select(v => v with { Form = config.CleanForm(v.Form) })
                    .ToList()))
            .ToList();
    }

    private static DictionaryEntry? SelectBestHeadword(
        IEnumerable<DictionaryEntry> entries, IReadOnlyList<string> candidates, string? selectedPartOfSpeech)
    {
        return OrderByQuality(entries, candidates, selectedPartOfSpeech).FirstOrDefault();
    }

    private static DictionaryEntry? SelectBestVariant(
        IReadOnlyList<(DictionaryEntry Entry, IReadOnlyList<WordDetailVariantViewModel> Variants)> parsed,
        IReadOnlyList<string> candidates,
        string? selectedPartOfSpeech)
    {
        var variantsByEntry = parsed.ToDictionary(p => p.Entry, p => p.Variants);

        foreach (var candidate in candidates)
        {
            var matches = parsed
                .Where(p => p.Variants.Any(v => VariantFormEquals(v.Form, candidate)))
                .Select(p => p.Entry);

            var best = OrderByQuality(matches, candidates, selectedPartOfSpeech, e => VariantMatchRank(variantsByEntry[e], candidate))
                .FirstOrDefault();
            if (best != null)
            {
                return best;
            }
        }
        return null;
    }

    private static IEnumerable<DictionaryEntry> OrderByQuality(
        IEnumerable<DictionaryEntry> entries,
        IReadOnlyList<string> candidates,
        string? selectedPartOfSpeech,
        Func<DictionaryEntry, int>? primaryRank = null)
    {
        primaryRank ??= entry => CandidateRank(candidates, entry.Word);
        return entries
            .OrderBy(primaryRank)
            .ThenBy(entry => PartOfSpeechRank(entry, selectedPartOfSpeech))
            .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.Description))
            .ThenByDescending(entry => !string.IsNullOrWhiteSpace(entry.ExampleSentence))
            .ThenBy(entry => entry.Word.Length);
    }

    private static int PartOfSpeechRank(DictionaryEntry entry, string? selectedPartOfSpeech)
    {
        if (string.IsNullOrWhiteSpace(selectedPartOfSpeech))
        {
            return string.IsNullOrWhiteSpace(entry.PartOfSpeech) ? 1 : 0;
        }

        return string.Equals(entry.PartOfSpeech, selectedPartOfSpeech, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }

    private static int VariantMatchRank(IReadOnlyList<WordDetailVariantViewModel> entryVariants, string form)
    {
        var variants = entryVariants
            .Where(variant => VariantFormEquals(variant.Form, form))
            .ToList();

        var exactVariants = variants
            .Where(variant => string.Equals(variant.Form, form, StringComparison.Ordinal))
            .ToList();
        if (exactVariants.Any(variant => variant.HasAnyTag("singular")))
        {
            return 0;
        }
        // Untagged variants outrank explicitly plural ones: treat unmarked as the canonical form.
        if (exactVariants.Any(variant => !variant.HasAnyTag("plural")))
        {
            return 1;
        }
        if (exactVariants.Count > 0)
        {
            return 2;
        }

        if (variants.Any(variant => variant.HasAnyTag("singular")))
        {
            return 3;
        }
        if (variants.Any(variant => !variant.HasAnyTag("plural")))
        {
            return 4;
        }
        return 5;
    }

    private static bool VariantFormEquals(string left, string right)
    {
        return string.Equals(left, right, StringComparison.Ordinal)
            || string.Equals(
                StripCombiningMarks(left),
                StripCombiningMarks(right),
                StringComparison.OrdinalIgnoreCase);
    }

    private static string StripCombiningMarks(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(ch);
            }
        }
        return builder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static DictionaryEntry? PreferVariantParent(DictionaryEntry? headwordMatch, DictionaryEntry? variantMatch)
    {
        if (headwordMatch == null) return variantMatch;
        if (variantMatch == null) return headwordMatch;
        return IsInflectionStub(headwordMatch) && !IsInflectionStub(variantMatch)
            ? variantMatch
            : headwordMatch;
    }

    private static bool IsInflectionStub(DictionaryEntry entry)
    {
        var properties = WordDetailJsonReader.ReadProperties(entry.Properties);
        var tags = properties
            .Where(property => string.Equals(property.Key, "tags", StringComparison.OrdinalIgnoreCase))
            .Select(property => property.Value)
            .FirstOrDefault() ?? string.Empty;

        return tags.Contains("form of", StringComparison.OrdinalIgnoreCase)
            || (entry.Description?.Contains(" of ", StringComparison.OrdinalIgnoreCase) == true
                && (entry.Description.Contains("inflection", StringComparison.OrdinalIgnoreCase)
                    || entry.Description.Contains("participle of", StringComparison.OrdinalIgnoreCase)
                    || entry.Description.Contains("connegative of", StringComparison.OrdinalIgnoreCase)));
    }

    private string? MatchSupportedLangCode(string? language)
    {
        if (string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        foreach (var config in _configs)
        {
            foreach (var alias in config.Aliases)
            {
                var matches = alias.Length <= 2
                    ? language.Equals(alias, StringComparison.OrdinalIgnoreCase)
                    : language.Contains(alias, StringComparison.OrdinalIgnoreCase);
                if (matches)
                {
                    return config.LangCode;
                }
            }
        }
        return null;
    }

    private static IReadOnlyList<string> GetManualDictionaryCandidates(string? word)
    {
        if (string.IsNullOrWhiteSpace(word))
        {
            return Array.Empty<string>();
        }

        var trimmed = word.Trim();
        var candidates = new List<string> { trimmed };

        var upperFirst = char.ToUpperInvariant(trimmed[0]) + trimmed[1..];
        if (!string.Equals(upperFirst, trimmed, StringComparison.Ordinal))
        {
            candidates.Add(upperFirst);
        }

        var lowerFirst = char.ToLowerInvariant(trimmed[0]) + trimmed[1..];
        if (!string.Equals(lowerFirst, trimmed, StringComparison.Ordinal)
            && !string.Equals(lowerFirst, upperFirst, StringComparison.Ordinal))
        {
            candidates.Add(lowerFirst);
        }

        return candidates;
    }

    private static int CandidateRank(IReadOnlyList<string> candidates, string word)
    {
        for (var index = 0; index < candidates.Count; index++)
        {
            if (string.Equals(candidates[index], word, StringComparison.Ordinal))
            {
                return index * 2;
            }
        }
        for (var index = 0; index < candidates.Count; index++)
        {
            if (string.Equals(candidates[index], word, StringComparison.OrdinalIgnoreCase))
            {
                return (index * 2) + 1;
            }
        }
        return candidates.Count * 2;
    }
}
