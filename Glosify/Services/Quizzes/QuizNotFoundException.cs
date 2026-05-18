namespace Glosify.Services;

public sealed class QuizNotFoundException : Exception
{
    public QuizNotFoundException()
        : base("Quiz not found or not owned by this user.") { }

    public QuizNotFoundException(string message)
        : base(message) { }
}
