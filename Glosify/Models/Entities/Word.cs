using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities
{
    [Table("words")]
    public class Word
    {
        [Column("id")]
        public string Id { get; set; } = string.Empty;

        [Column("quiz_id")]
        public Guid QuizId { get; set; }

        [Column("lemma")]
        public string Lemma { get; set; } = string.Empty;

        [Column("translation")]
        public string Translation { get; set; } = string.Empty;

        [Column("word_detail_id")]
        public string WordDetailId { get; set; } = string.Empty;
    }
}
