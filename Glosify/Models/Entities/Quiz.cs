namespace Glosify.Models.Entities
{
    public class Quiz
    {
        public Guid Id { get; set; }
        public string Name { get; set; }
        public Guid UserId { get; set; }
        public Guid? FolderId { get; set; }
        public DateTimeOffset CreatedAt { get; set; }
        public bool IsSongQuiz { get; set; }
        public string ProcessingStatus { get; set; }
        public string? ProcessingMessage { get; set; }
        public string SourceLanguage { get; set; }
        public string TargetLanguage { get; set; }
        public string Language { get; set; }
        public bool AnkiTrackingEnabled { get; set; }
        public bool AnkiTrackWordsForward { get; set; }
        public bool AnkiTrackWordsReverse { get; set; }
        public bool AnkiTrackSentencesForward { get; set; }
        public bool AnkiTrackSentencesReverse { get; set; }
        public bool IsPublic { get; set; }
        public Guid? OriginalQuizId { get; set; }
    }
}
