using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities
{
    [Table("word_details")]
    public class WordDetail
    {
        [Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("source_language")]
        public string SourceLanguage { get; set; } = string.Empty;

        [Column("target_language")]
        public string TargetLanguage { get; set; } = string.Empty;

        [Column("word")]
        public string Word { get; set; } = string.Empty;

        [Column("translation")]
        public string Translation { get; set; } = string.Empty;

        [Column("normalized_word")]
        public string NormalizedWord { get; set; } = string.Empty;

        [Column("normalized_translation")]
        public string NormalizedTranslation { get; set; } = string.Empty;

        [Column("normalized_word_hash")]
        public string NormalizedWordHash { get; set; } = string.Empty;

        [Column("normalized_translation_hash")]
        public string NormalizedTranslationHash { get; set; } = string.Empty;

        [Column("properties")]
        public string Properties { get; set; } = "{}";

        [Column("example_sentence")]
        public string ExampleSentence { get; set; } = string.Empty;

        [Column("example_sentence_translation")]
        public string ExampleSentenceTranslation { get; set; } = string.Empty;

        [Column("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [Column("variants")]
        public string Variants { get; set; } = "[]";

        [Column("language")]
        public string Language { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; }

        [Column("updated_at")]
        public DateTimeOffset UpdatedAt { get; set; }
    }
}
