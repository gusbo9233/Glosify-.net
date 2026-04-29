using System.Text.Json;
using Glosify.Models.LanguageConfig;

namespace Glosify.Models
{
    public class WordDetailViewModel
    {
        public WordDetail Detail { get; set; } = null!;
        public Word? Word { get; set; }
        public Quiz Quiz { get; set; } = null!;
        public DictionaryEntry? DictionaryMatch { get; set; }
        public IReadOnlyList<KeyValuePair<string, string>> Properties { get; set; } = [];
        public IReadOnlyList<WordDetailVariantViewModel> Variants { get; set; } = [];
        public WordClassConfig? WordClassConfig { get; set; }

        public string PartOfSpeech => GetProperty("pos");
        public bool HasDictionaryMatch => DictionaryMatch is not null;
        public string Explanation => !string.IsNullOrWhiteSpace(Detail.Explanation)
            ? Detail.Explanation
            : DictionaryMatch?.Description ?? string.Empty;

        public string ExampleSentence => !string.IsNullOrWhiteSpace(Detail.ExampleSentence)
            ? Detail.ExampleSentence
            : DictionaryMatch?.ExampleSentence ?? string.Empty;

        public string GetProperty(string key)
        {
            return Properties
                .FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;
        }

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

        public static IReadOnlyList<WordDetailVariantViewModel> FilterByTags(
            IReadOnlyList<WordDetailVariantViewModel> variants, IReadOnlyList<string> requiredTags)
        {
            if (variants.Count == 0 || requiredTags.Count == 0)
                return variants;

            return variants
                .Where(v => requiredTags.All(tag => v.HasAnyTag(tag)))
                .ToList();
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

            if (identityTags.Length == 0)
                return variants;

            var filtered = variants
                .Where(v => identityTags.All(tag => v.HasAnyTag(tag)))
                .ToList();

            return filtered.Count == 0 ? variants : filtered;
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

        private static WordDetailVariantViewModel ReadVariant(JsonElement element)
        {
            if (element.ValueKind != JsonValueKind.Object)
                return new WordDetailVariantViewModel(ReadElement(element), []);

            var form = element.TryGetProperty("form", out var formElement)
                ? ReadElement(formElement)
                : string.Empty;

            var tags = element.TryGetProperty("tags", out var tagsElement) && tagsElement.ValueKind == JsonValueKind.Array
                ? tagsElement.EnumerateArray().Select(ReadElement).Where(tag => !string.IsNullOrWhiteSpace(tag)).ToArray()
                : [];

            return new WordDetailVariantViewModel(form, tags);
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

        public static string Humanize(string value)
        {
            return value.Replace("_", " ").Replace("-", " ");
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

    public sealed record WordDetailVariantViewModel(string Form, IReadOnlyList<string> Tags)
    {
        public bool HasAnyTag(params string[] tags)
        {
            return Tags.Any(existing => tags.Any(tag => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
