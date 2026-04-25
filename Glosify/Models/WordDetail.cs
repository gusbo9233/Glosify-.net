using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models
{
    [Table("word_details")]
    public class WordDetail
    {
        [Column("quiz_id")]
        public Guid QuizId { get; set; }

        [Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("properties")]
        public string Properties { get; set; } = "{}";

        [Column("example_sentence")]
        public string ExampleSentence { get; set; } = string.Empty;

        [Column("explanation")]
        public string Explanation { get; set; } = string.Empty;

        [Column("variants")]
        public string Variants { get; set; } = "[]";

        [Column("language")]
        public string Language { get; set; } = string.Empty;
    }
}
