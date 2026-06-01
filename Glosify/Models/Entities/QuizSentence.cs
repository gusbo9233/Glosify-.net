using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities
{
    [Table("quiz_sentences")]
    public class QuizSentence
    {
        [Column("id")]
        public Guid Id { get; set; }

        [Column("quiz_id")]
        public Guid QuizId { get; set; }

        [Column("text")]
        public string Text { get; set; } = string.Empty;

        [Column("translation")]
        public string Translation { get; set; } = string.Empty;

        [Column("created_at")]
        public DateTimeOffset CreatedAt { get; set; }
    }
}
