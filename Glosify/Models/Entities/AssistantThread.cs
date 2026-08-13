using System.ComponentModel.DataAnnotations.Schema;

namespace Glosify.Models.Entities;

[Table("assistant_threads")]
public class AssistantThread
{
    [Column("id")]
    public Guid Id { get; set; }

    [Column("quiz_id")]
    public Guid? QuizId { get; set; }

    [Column("context_quiz_id")]
    public Guid? ContextQuizId { get; set; }

    [Column("context_transcript_id")]
    public Guid? ContextTranscriptId { get; set; }

    [Column("context_book_document_id")]
    public Guid? ContextBookDocumentId { get; set; }

    [Column("user_id")]
    public string UserId { get; set; } = string.Empty;

    /// <summary>
    /// The learning language the chat was started in, so the saved chat list only
    /// shows the conversations belonging to the language the user has selected.
    /// </summary>
    [Column("language")]
    public string? Language { get; set; }

    /// <summary>
    /// The language this conversation is held in, which is not necessarily the language being
    /// learned or the one quizzes translate into.
    /// </summary>
    /// <remarks>
    /// Recorded once when the chat starts so the assistant stops asking. <see cref="Language"/>
    /// stays the learning-language grouping key and is unaffected.
    /// </remarks>
    [Column("conversation_language")]
    public string? ConversationLanguage { get; set; }

    [Column("title")]
    public string Title { get; set; } = string.Empty;

    [Column("created_at")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at")]
    public DateTimeOffset UpdatedAt { get; set; }

    [Column("active_turn_id")]
    public Guid? ActiveTurnId { get; set; }

    [Column("active_turn_expires_at")]
    public DateTime? ActiveTurnExpiresAt { get; set; }
}
