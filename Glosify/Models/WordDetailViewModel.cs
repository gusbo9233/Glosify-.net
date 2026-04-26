using System.Text.Json;

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
    }

    public sealed record WordDetailVariantViewModel(string Form, IReadOnlyList<string> Tags)
    {
        public bool HasAnyTag(params string[] tags)
        {
            return Tags.Any(existing => tags.Any(tag => string.Equals(existing, tag, StringComparison.OrdinalIgnoreCase)));
        }
    }
}
