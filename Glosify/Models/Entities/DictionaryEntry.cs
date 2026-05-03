using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities
{
    [Table("dictionary_entries")]
    public class DictionaryEntry
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("source_hash")]
        public string SourceHash { get; set; } = string.Empty;

        [Column("word")]
        public string Word { get; set; } = string.Empty;

        [Column("language")]
        public string Language { get; set; } = string.Empty;

        [Column("lang_code")]
        public string LangCode { get; set; } = string.Empty;

        [Column("part_of_speech")]
        public string PartOfSpeech { get; set; } = string.Empty;

        [Column("properties")]
        public string Properties { get; set; } = "{}";

        [Column("variants")]
        public string Variants { get; set; } = "[]";

        [Column("description")]
        public string? Description { get; set; }

        [Column("example_sentence")]
        public string? ExampleSentence { get; set; }

        [Column("source")]
        public string Source { get; set; } = "kaikki";

        [Column("imported_at")]
        public DateTimeOffset ImportedAt { get; set; }
    }
}
