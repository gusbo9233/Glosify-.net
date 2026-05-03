namespace Glosify.Models.Requests
{
    public class TypingAnswer
    {
        public Guid WordId { get; set; }
        public string UserAnswer { get; set; } = string.Empty;
        public string CorrectAnswer { get; set; } = string.Empty;
    }
}
