using Glosify.Data;
using Glosify.Models;
using Glosify.Models.LanguageConfig;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class WordDetailViewModelService : IWordDetailViewModelService
{
    private readonly GlosifyContext _context;
    private readonly IReadOnlyDictionary<string, ILanguageDictionaryConfig> _languageConfigs;

    public WordDetailViewModelService(
        GlosifyContext context,
        IEnumerable<ILanguageDictionaryConfig> languageConfigs)
    {
        _context = context;
        _languageConfigs = languageConfigs.ToDictionary(c => c.LangCode, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<WordDetailViewModel?> BuildAsync(string wordDetailId, string userId)
    {
        var owned = await LoadAccessibleAsync(wordDetailId, userId);
        if (owned == null)
        {
            return null;
        }
        var (wordDetail, word, quiz) = owned.Value;

        var properties = WordDetailJsonReader.ReadProperties(wordDetail.Properties);
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

        var rawVariants = WordDetailJsonReader.ReadVariants(wordDetail.Variants);
        if (languageConfig != null)
        {
            // Defensive cleanup carried over from the prior dictionary path; harmless on Gemini output.
            rawVariants = rawVariants
                .Where(v => !languageConfig.IsJunkVariant(v))
                .Select(v => v with { Form = languageConfig.CleanForm(v.Form) })
                .ToList();
        }

        var filteredVariants = rawVariants;
        if (pos == "Pronoun" && languageConfig?.BundlesPronounParadigm == true)
        {
            filteredVariants = WordDetailJsonReader.FilterPronounParadigm(filteredVariants, word?.Lemma);
        }

        return new WordDetailViewModel
        {
            Detail = wordDetail,
            Word = word,
            Quiz = quiz,
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
            where detail.Id == id && quiz.UserId == userId
            select new { detail, word, quiz }).FirstOrDefaultAsync();

        return pair == null ? null : (pair.detail, pair.word, pair.quiz);
    }
}
