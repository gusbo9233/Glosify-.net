namespace Glosify.Models.Entities
{
    public class Quiz
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string UserId { get; set; } = string.Empty;
        public Guid? FolderId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsSongQuiz { get; set; }
        public string ProcessingStatus { get; set; } = string.Empty;
        public string? ProcessingMessage { get; set; }
        public string SourceLanguage { get; set; } = string.Empty;
        public string TargetLanguage { get; set; } = string.Empty;
        public string Language { get; set; } = string.Empty;
        public bool AnkiTrackingEnabled { get; set; }
        public bool AnkiTrackWordsForward { get; set; }
        public bool AnkiTrackWordsReverse { get; set; }
        public bool AnkiTrackSentencesForward { get; set; }
        public bool AnkiTrackSentencesReverse { get; set; }
        public bool IsPublic { get; set; }
        public Guid? OriginalQuizId { get; set; }
    }
}
