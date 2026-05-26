using System.Text.Json;

namespace Glosify.Models.Entities;

public static class WordDetailJsonReader
{
    public static IReadOnlyList<KeyValuePair<string, string>> ReadProperties(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];

            return document.RootElement
                .EnumerateObject()
                .Select(property => new KeyValuePair<string, string>(property.Name, ReadElement(property.Value)))
                .Where(property => !string.IsNullOrWhiteSpace(property.Value))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<WordDetailVariantViewModel> ReadVariants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return [];

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
                return [];

            return document.RootElement
                .EnumerateArray()
                .Select(ReadVariant)
                .Where(variant => !string.IsNullOrWhiteSpace(variant.Form))
                .ToList();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public static IReadOnlyList<WordDetailVariantViewModel> FilterPronounParadigm(
        IReadOnlyList<WordDetailVariantViewModel> variants, string? lemma)
    {
        if (variants.Count == 0 || string.IsNullOrWhiteSpace(lemma))
            return variants;

        var identityTags = variants
            .Where(v => string.Equals(v.Form, lemma, StringComparison.OrdinalIgnoreCase))
            .SelectMany(v => v.Tags)
            .Where(IsPronounIdentityTag)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (identityTags.Length > 0)
        {
            var filtered = variants
                .Where(v => identityTags.All(tag => v.HasAnyTag(tag)))
                .ToList();
            if (filtered.Count > 0)
                return filtered;
        }

        // Wiktionary often bundles all personal pronouns into one entry's variants
        // without per-form person/number tags. Detect that by spotting multiple distinct
        // forms sharing the same tag-set (e.g. two different "nominative" forms) and fall
        // back to showing only the lemma's own forms — better an under-filled paradigm
        // than a soup of every pronoun in the language.
        var bundled = variants
            .GroupBy(v => string.Join("|", v.Tags.OrderBy(t => t, StringComparer.Ordinal)), StringComparer.Ordinal)
            .Any(g => g.Select(v => v.Form).Distinct(StringComparer.OrdinalIgnoreCase).Count() > 1);

        if (bundled)
        {
            var lemmaOnly = variants
                .Where(v => string.Equals(v.Form, lemma, StringComparison.OrdinalIgnoreCase))
                .ToList();
            return lemmaOnly.Count > 0 ? lemmaOnly : variants;
        }

        return variants;
    }

    public static string Humanize(string value)
    {
        return value.Replace("_", " ").Replace("-", " ");
    }

    private static WordDetailVariantViewModel ReadVariant(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return new WordDetailVariantViewModel(ReadElement(element), []);

        var form = element.TryGetProperty("form", out var formElement)
            ? ReadElement(formElement)
            : string.Empty;

        var label = element.TryGetProperty("label", out var labelElement)
            ? ReadElement(labelElement)
            : string.Empty;

        var group = element.TryGetProperty("group", out var groupElement)
            ? ReadElement(groupElement)
            : string.Empty;

        var tags = element.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
            ? tagsElement.EnumerateArray().Select(ReadElement).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray()
            : [];

        return new WordDetailVariantViewModel(form, tags, label, group);
    }

    private static string ReadElement(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.ToString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Array => string.Join(", ", element.EnumerateArray().Select(ReadElement)),
            JsonValueKind.Object => string.Join(", ", element.EnumerateObject().Select(property => $"{Humanize(property.Name)}: {ReadElement(property.Value)}")),
            _ => string.Empty
        };
    }

    private static bool IsPronounIdentityTag(string tag)
    {
        return tag.Equals("first-person", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("second-person", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("third-person", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("singular", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("plural", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("masculine", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("feminine", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("neuter", StringComparison.OrdinalIgnoreCase)
            || tag.Equals("reflexive", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed record WordDetailVariantViewModel(
    string Form,
    IReadOnlyList<string> Tags,
    string Label = "",
    string Group = "")
{
    public bool HasAnyTag(params string[] tags)
    {
        return Tags.Any(existing => tags.Any(tag => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)));
    }
}
