using System.Text.Json;
using Glosify.Data;
using Glosify.Models;

namespace Glosify.Services;

public sealed class WordDetailEnrichmentService : IWordDetailEnrichmentService
{
    private readonly GlosifyContext _context;
    private readonly IAiWordGenerationService _ai;

    public WordDetailEnrichmentService(GlosifyContext context, IAiWordGenerationService ai)
    {
        _context = context;
        _ai = ai;
    }

    public async Task<bool> EnrichAsync(
        WordDetail detail,
        Word? word,
        Quiz? quiz,
        string fallbackWord,
        string fallbackTargetLanguage,
        CancellationToken cancellationToken = default)
    {
        if (!NeedsEnrichment(detail))
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

        var generated = await _ai.GenerateWordDetailAsync(
            lookupWord,
            translation,
            sourceLanguage,
            targetLanguage);
        if (generated == null)
        {
            return false;
        }

        var properties = SerializeProperties(generated.Properties);
        var variants = JsonSerializer.Serialize(generated.Variants);
        var explanation = generated.Explanation?.Trim() ?? string.Empty;
        var exampleSentence = generated.ExampleSentence?.Trim() ?? string.Empty;

        return ApplyGenerated(detail, properties, variants, explanation, exampleSentence);
    }

    private static bool NeedsEnrichment(WordDetail detail)
    {
        return detail.Properties == "{}"
            || detail.Variants == "[]"
            || string.IsNullOrWhiteSpace(detail.Explanation)
            || string.IsNullOrWhiteSpace(detail.ExampleSentence);
    }

    private static bool ApplyGenerated(
        WordDetail detail,
        string properties,
        string variants,
        string explanation,
        string exampleSentence)
    {
        var changed = false;
        if (detail.Properties == "{}")
        {
            detail.Properties = string.IsNullOrWhiteSpace(properties) ? "{}" : properties;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (detail.Variants == "[]")
        {
            detail.Variants = string.IsNullOrWhiteSpace(variants) ? "[]" : variants;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(detail.Explanation) && !string.IsNullOrWhiteSpace(explanation))
        {
            detail.Explanation = explanation;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }
        if (string.IsNullOrWhiteSpace(detail.ExampleSentence) && !string.IsNullOrWhiteSpace(exampleSentence))
        {
            detail.ExampleSentence = exampleSentence;
            detail.UpdatedAt = DateTimeOffset.UtcNow;
            changed = true;
        }

        return changed;
    }

    private static string SerializeProperties(Dictionary<string, JsonElement> properties)
    {
        return properties.Count == 0 ? "{}" : JsonSerializer.Serialize(properties);
    }

}
