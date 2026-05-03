using Glosify.Models.LanguageConfig;

namespace Glosify.Models.ViewModels
{
    public class WordDetailViewModel
    {
        public WordDetail Detail { get; set; } = null!;
        public Word? Word { get; set; }
        public Quiz Quiz { get; set; } = null!;
        public IReadOnlyList<KeyValuePair<string, string>> Properties { get; set; } = [];
        public IReadOnlyList<WordDetailVariantViewModel> Variants { get; set; } = [];
        public WordClassConfig? WordClassConfig { get; set; }

        public string PartOfSpeech => GetProperty("pos");
        public string Explanation => Detail.Explanation ?? string.Empty;
        public string ExampleSentence => Detail.ExampleSentence ?? string.Empty;

        public string GetProperty(string key)
        {
            return Properties
                .FirstOrDefault(property => string.Equals(property.Key, key, StringComparison.OrdinalIgnoreCase))
                .Value ?? string.Empty;
        }
    }
}
