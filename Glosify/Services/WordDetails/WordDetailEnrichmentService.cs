using System.Text.Json;
using System.Text.RegularExpressions;
using Glosify.Models;

namespace Glosify.Services;

public sealed class WordDetailEnrichmentService : IWordDetailEnrichmentService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Regex TagSeparator = new(@"[,;/]+", RegexOptions.Compiled);
    private static readonly Regex PropertySeparator = new(@"[\s-]+", RegexOptions.Compiled);
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex WordPattern = new(@"[\p{L}\p{M}\p{N}]+", RegexOptions.Compiled);

    private readonly IVocabularyGenerationService _vocabularyGenerator;

    public WordDetailEnrichmentService(IVocabularyGenerationService vocabularyGenerator)
    {
        _vocabularyGenerator = vocabularyGenerator;
    }

    public async Task<bool> EnrichAsync(
        WordDetail detail,
        Word? word,
        Quiz? quiz,
        string fallbackWord,
        string fallbackTargetLanguage,
        CancellationToken cancellationToken = default,
        bool force = false)
    {
        if (!force && !NeedsEnrichment(detail))
        {
            return false;
        }

        var lookupWord = (word?.Lemma ?? fallbackWord).Trim();
        var translation = (word?.Translation ?? string.Empty).Trim();
        var sourceLanguage = (quiz?.SourceLanguage ?? "English").Trim();
        var targetLanguage = (string.IsNullOrWhiteSpace(detail.Language)
            ? quiz?.TargetLanguage ?? fallbackTargetLanguage
            : detail.Language).Trim();

        if (string.IsNullOrWhiteSpace(lookupWord) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        var generated = await _vocabularyGenerator.GenerateWordDetailAsync(
            lookupWord,
            translation,
            sourceLanguage,
            targetLanguage,
            cancellationToken);
        if (generated == null)
        {
            return false;
        }

        var properties = SerializeProperties(generated.Properties);
        var variants = SerializeVariants(generated.Variants);
        var explanation = generated.Explanation?.Trim() ?? string.Empty;
        var exampleSentence = generated.ExampleSentence?.Trim() ?? string.Empty;
        var exampleSentenceTranslation = generated.ExampleSentenceTranslation?.Trim() ?? string.Empty;

        return ApplyGenerated(detail, properties, variants, explanation, exampleSentence, exampleSentenceTranslation, force);
    }

    private static bool NeedsEnrichment(WordDetail detail)
    {
        return IsEmptyJsonObject(detail.Properties)
            || IsEmptyJsonArray(detail.Variants)
            || string.IsNullOrWhiteSpace(detail.Explanation)
            || string.IsNullOrWhiteSpace(detail.ExampleSentence);
    }

    private static bool ApplyGenerated(
        WordDetail detail,
        string properties,
        string variants,
        string explanation,
        string exampleSentence,
        string exampleSentenceTranslation,
        bool force)
    {
        var changed = false;
        var needsGeneratedText = IsEmptyJsonObject(detail.Properties) || IsEmptyJsonArray(detail.Variants);

        if (force || (IsEmptyJsonObject(detail.Properties) && !IsEmptyJsonObject(properties)))
        {
            detail.Properties = properties;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (force || (IsEmptyJsonArray(detail.Variants) && !IsEmptyJsonArray(variants)))
        {
            detail.Variants = variants;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if ((force || needsGeneratedText || string.IsNullOrWhiteSpace(detail.Explanation))
            && !string.IsNullOrWhiteSpace(explanation)
            && !string.Equals(detail.Explanation?.Trim(), explanation, StringComparison.Ordinal))
        {
            detail.Explanation = explanation;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (!IsUsefulExampleSentence(exampleSentence, explanation))
        {
            exampleSentence = string.Empty;
            exampleSentenceTranslation = string.Empty;
        }

        if ((force || string.IsNullOrWhiteSpace(detail.ExampleSentence))
            && !string.IsNullOrWhiteSpace(exampleSentence)
            && !string.Equals(detail.ExampleSentence?.Trim(), exampleSentence, StringComparison.Ordinal))
        {
            detail.ExampleSentence = exampleSentence;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if ((force || string.IsNullOrWhiteSpace(detail.ExampleSentenceTranslation))
            && !string.IsNullOrWhiteSpace(exampleSentenceTranslation)
            && !string.Equals(detail.ExampleSentenceTranslation?.Trim(), exampleSentenceTranslation, StringComparison.Ordinal))
        {
            detail.ExampleSentenceTranslation = exampleSentenceTranslation;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        return changed;
    }

    private static string SerializeProperties(Dictionary<string, JsonElement> properties)
    {
        if (properties.Count == 0)
        {
            return "{}";
        }

        var normalized = new Dictionary<string, JsonElement>();
        foreach (var (key, value) in properties)
        {
            if (value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            normalized[NormalizePropertyKey(key)] = value.Clone();
        }

        return normalized.Count == 0 ? "{}" : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static string SerializeVariants(IEnumerable<GeneratedWordVariant> variants)
    {
        var normalized = variants
            .Select(variant => new GeneratedWordVariant
            {
                Form = variant.Form?.Trim(),
                Tags = NormalizeTags(variant.Tags)
            })
            .Where(variant => !string.IsNullOrWhiteSpace(variant.Form) && variant.Tags.Count > 0)
            .ToList();

        return normalized.Count == 0 ? "[]" : JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static List<string> NormalizeTags(IEnumerable<string> tags)
    {
        var normalized = new List<string>();

        foreach (var tag in tags)
        {
            foreach (var token in ExpandTag(tag))
            {
                if (!normalized.Contains(token, StringComparer.OrdinalIgnoreCase))
                {
                    normalized.Add(token);
                }
            }
        }

        return normalized;
    }

    private static IEnumerable<string> ExpandTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            yield break;
        }

        var normalized = Whitespace.Replace(tag.Trim().ToLowerInvariant().Replace("_", " "), " ");
        normalized = normalized
            .Replace("non male personal", "non-masculine-personal")
            .Replace("non-male personal", "non-masculine-personal")
            .Replace("non masculine personal", "non-masculine-personal")
            .Replace("non-masculine personal", "non-masculine-personal")
            .Replace("non masculine-personal", "non-masculine-personal")
            .Replace("nonvirile", "non-masculine-personal")
            .Replace("virile", "masculine-personal")
            .Replace("male personal", "masculine-personal")
            .Replace("male-personal", "masculine-personal")
            .Replace("masculine personal", "masculine-personal")
            .Replace("masculine-personal plural", "masculine-personal plural")
            .Replace("female personal", "non-masculine-personal")
            .Replace("female-personal", "non-masculine-personal")
            .Replace("female group", "non-masculine-personal")
            .Replace("female plural", "non-masculine-personal plural")
            .Replace("feminine plural", "non-masculine-personal plural")
            .Replace("non past", "non-past")
            .Replace("ma infinitive", "ma-infinitive")
            .Replace("da infinitive", "da-infinitive")
            .Replace("1st person", "first-person")
            .Replace("2nd person", "second-person")
            .Replace("3rd person", "third-person")
            .Replace("first person", "first-person")
            .Replace("second person", "second-person")
            .Replace("third person", "third-person");

        foreach (var part in TagSeparator.Split(normalized))
        {
            foreach (var token in Whitespace.Split(part.Trim()))
            {
                var mapped = MapTagToken(token);
                if (!string.IsNullOrWhiteSpace(mapped))
                {
                    yield return mapped;
                }
            }
        }
    }

    private static string MapTagToken(string token)
    {
        return token.Trim('-', '.', ':') switch
        {
            "" or "tense" or "case" or "form" or "forms" => string.Empty,
            "nom" or "nom." => "nominative",
            "gen" or "gen." => "genitive",
            "dat" or "dat." => "dative",
            "acc" or "acc." => "accusative",
            "instr" or "inst" or "ins" => "instrumental",
            "loc" or "loc." => "locative",
            "voc" or "voc." => "vocative",
            "part" or "part." => "partitive",
            "sg" or "sing" or "sing." => "singular",
            "pl" or "plur" or "plur." => "plural",
            "masc" or "masc." => "masculine",
            "fem" or "fem." => "feminine",
            "neut" or "neut." => "neuter",
            "1" or "1st" => "first-person",
            "2" or "2nd" => "second-person",
            "3" or "3rd" => "third-person",
            _ => token.Replace(" ", "-")
        };
    }

    private static string NormalizePropertyKey(string key)
    {
        var normalized = PropertySeparator
            .Replace(key.Trim().ToLowerInvariant(), "_")
            .Trim('_');
        var compact = normalized.Replace("_", string.Empty);

        return compact switch
        {
            "pos" or "partofspeech" or "wordclass" => "pos",
            "grammaticalgender" => "gender",
            _ => normalized
        };
    }

    private static bool IsEmptyJsonObject(string? json)
        => !WordDetailJsonReader.ReadProperties(json).Any();

    private static bool IsEmptyJsonArray(string? json)
        => !WordDetailJsonReader.ReadVariants(json).Any();

    private static bool IsUsefulExampleSentence(string? exampleSentence, string? explanation)
    {
        if (string.IsNullOrWhiteSpace(exampleSentence))
        {
            return false;
        }

        var trimmed = exampleSentence.Trim();
        if (WordPattern.Matches(trimmed).Count < 2)
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(explanation)
            || !string.Equals(trimmed, explanation.Trim(), StringComparison.OrdinalIgnoreCase);
    }

}
