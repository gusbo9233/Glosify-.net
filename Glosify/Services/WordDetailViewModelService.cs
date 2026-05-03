using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class WordDetailViewModelService : IWordDetailViewModelService
{
    private readonly GlosifyContext _context;
    private readonly IDictionaryService _dictionaryService;
    private readonly IReadOnlyDictionary<string, ILanguageDictionaryConfig> _languageConfigs;
    private readonly IWordDetailEnrichmentService? _enrichmentService;

    public WordDetailViewModelService(
        GlosifyContext context,
        IDictionaryService dictionaryService,
        IEnumerable<ILanguageDictionaryConfig> languageConfigs,
        IWordDetailEnrichmentService? enrichmentService = null)
    {
        _context = context;
        _dictionaryService = dictionaryService;
        _languageConfigs = languageConfigs.ToDictionary(c => c.LangCode, StringComparer.OrdinalIgnoreCase);
        _enrichmentService = enrichmentService;
    }

    public async Task<WordDetailViewModel?> BuildAsync(string wordDetailId, string userId)
    {
        var owned = await LoadAccessibleAsync(wordDetailId, userId);
        if (owned == null)
        {
            return null;
        }
        var (wordDetail, word, quiz) = owned.Value;

        await TryEnrichAsync(wordDetail, word, quiz);

        var detailProperties = WordDetailJsonReader.ReadProperties(wordDetail.Properties);
        var dictionaryMatch = await ResolveDictionaryMatchAsync(wordDetail, word, quiz, null);
        var dictionaryProperties = WordDetailJsonReader.ReadProperties(dictionaryMatch?.Properties);
        var properties = MergeProperties(dictionaryProperties, detailProperties);
        var pos = LanguageResolver.NormalizePartOfSpeech(
            properties.FirstOrDefault(p => string.Equals(p.Key, "pos", StringComparison.OrdinalIgnoreCase)).Value);

        var detailLanguage = string.IsNullOrWhiteSpace(wordDetail.Language) ? wordDetail.TargetLanguage : wordDetail.Language;
        var langCode = LanguageResolver.ResolveLangCode(detailLanguage);

        ILanguageDictionaryConfig? languageConfig = null;
        WordClassConfig? wordClassConfig = null;
        if (langCode != null && _languageConfigs.TryGetValue(langCode, out var resolved))
        {
            languageConfig = resolved;
            if (!string.IsNullOrEmpty(pos))
            {
                wordClassConfig = languageConfig.GetWordClass(pos);
            }
        }

        if (dictionaryMatch != null && !string.IsNullOrWhiteSpace(pos))
        {
            dictionaryMatch = await ResolveDictionaryMatchAsync(wordDetail, word, quiz, pos) ?? dictionaryMatch;
        }

        var detailVariants = WordDetailJsonReader.ReadVariants(wordDetail.Variants);
        var dictionaryVariants = WordDetailJsonReader.ReadVariants(dictionaryMatch?.Variants);
        var rawVariants = detailVariants.Any() ? detailVariants : dictionaryVariants;

        // Filter junk header rows / class markers and clean per-language quirks (e.g. strip
        // Ukrainian's parenthesized romanization tail) before any further filtering.
        if (languageConfig != null)
        {
            rawVariants = rawVariants
                .Where(v => !languageConfig.IsJunkVariant(v))
                .Select(v => v with { Form = languageConfig.CleanForm(v.Form) })
                .ToList();
        }

        var filteredVariants = wordClassConfig != null
            ? WordDetailJsonReader.FilterByTags(rawVariants, wordClassConfig.VariantTagFilters)
            : rawVariants;
        if (pos == "Pronoun" && languageConfig?.BundlesPronounParadigm == true)
        {
            filteredVariants = WordDetailJsonReader.FilterPronounParadigm(
                filteredVariants, word?.Lemma ?? dictionaryMatch?.Word);
        }

        return new WordDetailViewModel
        {
            Detail = wordDetail,
            Word = word,
            Quiz = quiz,
            DictionaryMatch = dictionaryMatch,
            Properties = properties,
            Variants = filteredVariants,
            WordClassConfig = wordClassConfig,
        };
    }

    private async Task<(WordDetail Detail, Word Word, Quiz Quiz)?> LoadAccessibleAsync(string id, string userId)
    {
        var pair = await (
            from word in _context.Words
            join quiz in _context.Quizzes on word.QuizId equals quiz.Id
            join detail in _context.WordDetails on word.WordDetailId equals detail.Id
            where detail.Id == id && quiz.UserId.ToString() == userId
            select new { detail, word, quiz }).FirstOrDefaultAsync();

        return pair == null ? null : (pair.detail, pair.word, pair.quiz);
    }

    private async Task TryEnrichAsync(WordDetail wordDetail, Word? word, Quiz quiz)
    {
        if (_enrichmentService == null || word == null)
        {
            return;
        }

        var shouldGenerate = wordDetail.Properties == "{}"
            || wordDetail.Variants == "[]"
            || string.IsNullOrWhiteSpace(wordDetail.Explanation)
            || string.IsNullOrWhiteSpace(wordDetail.ExampleSentence);
        if (!shouldGenerate)
        {
            return;
        }

        try
        {
            if (await _enrichmentService.EnrichAsync(
                wordDetail,
                word,
                quiz,
            word.Lemma,
            wordDetail.TargetLanguage))
        {
            await _context.SaveChangesAsync();
        }
        }
        catch (Exception)
        {
            // Details remain editable even if enrichment is unavailable or returns malformed JSON.
        }
    }

    private async Task<DictionaryEntry?> ResolveDictionaryMatchAsync(
        WordDetail wordDetail, Word? word, Quiz quiz, string? selectedPartOfSpeech)
    {
        if (word == null)
        {
            return null;
        }

        // Try detail language first, then quiz target language. Ukrainian/German/etc. resolve
        // through the configured aliases inside DictionaryService.
        var languageCandidates = new[]
        {
            wordDetail.Language,
            wordDetail.TargetLanguage,
            quiz.TargetLanguage,
            quiz.Language,
            quiz.SourceLanguage,
        };

        foreach (var language in languageCandidates)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                continue;
            }

            var match = await _dictionaryService.FindBestDictionaryMatchAsync(language, word.Lemma, selectedPartOfSpeech);
            if (match != null)
            {
                return match;
            }
        }

        return null;
    }

    private static IReadOnlyList<KeyValuePair<string, string>> MergeProperties(
        IReadOnlyList<KeyValuePair<string, string>> dictionaryProperties,
        IReadOnlyList<KeyValuePair<string, string>> detailProperties)
    {
        if (dictionaryProperties.Count == 0)
        {
            return detailProperties;
        }

        if (detailProperties.Count == 0)
        {
            return dictionaryProperties;
        }

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in dictionaryProperties.Concat(detailProperties))
        {
            merged[property.Key] = property.Value;
        }

        return merged.Select(property => new KeyValuePair<string, string>(property.Key, property.Value)).ToList();
    }
}
