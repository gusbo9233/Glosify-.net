using Glosify.Services.Quizzes;
using Microsoft.Extensions.Caching.Memory;

namespace Glosify.Services.Typing;

public class TypingSessionService : QuizSessionStore<TypingSessionData>, ITypingSessionService
{
    private readonly ITypingQuizService _typingQuizService;

    public TypingSessionService(IMemoryCache cache, ITypingQuizService typingQuizService, IQuizSessionRegistry registry)
        : base(cache, registry)
    {
        _typingQuizService = typingQuizService;
    }

    protected override string Mode => "typing";
    protected override string CacheKeyPrefix => "typing-quiz";

    protected override bool IsComplete(TypingSessionData session)
        => session.CurrentIndex >= session.Words.Count;

    public TypingSessionData StartSession(
        string userId,
        Guid quizId,
        string quizName,
        string sourceLanguage,
        string targetLanguage,
        int wordCount,
        IReadOnlyList<TypingWordData> words,
        string? practiceDirection = null,
        string? practiceItemType = null)
    {
        var normalizedDirection = PracticeDirection.Normalize(practiceDirection);
        var normalizedItemType = PracticeItemType.Normalize(practiceItemType);
        return new TypingSessionData
        {
            SessionId = Guid.NewGuid().ToString("N"),
            UserId = userId,
            QuizId = quizId,
            QuizName = quizName,
            SourceLanguage = sourceLanguage,
            TargetLanguage = targetLanguage,
            PracticeDirection = normalizedDirection,
            PromptLanguage = PracticeDirection.PromptLanguage(normalizedDirection, sourceLanguage, targetLanguage),
            AnswerLanguage = PracticeDirection.AnswerLanguage(normalizedDirection, sourceLanguage, targetLanguage),
            PracticeItemType = normalizedItemType,
            WordCount = Math.Clamp(wordCount, 1, 100),
            Words = words
        };
    }

    public TypingAnswerResult SubmitAnswer(TypingSessionData session, string userAnswer)
    {
        if (session.CurrentIndex >= session.Words.Count)
        {
            return new TypingAnswerResult();
        }

        var currentWord = session.Words[session.CurrentIndex];
        var isCorrect = _typingQuizService.CheckAnswer(userAnswer, currentWord.Answer);
        if (isCorrect)
        {
            session.CorrectCount++;
        }
        else
        {
            session.IncorrectCount++;
            session.IncorrectWords.Add(currentWord);
        }

        session.CurrentIndex++;
        var nextWord = session.CurrentIndex < session.Words.Count
            ? session.Words[session.CurrentIndex]
            : null;

        return new TypingAnswerResult
        {
            IsCorrect = isCorrect,
            CorrectAnswer = currentWord.Answer,
            ExampleSentence = currentWord.ExampleSentence,
            ExampleTranslation = currentWord.ExampleTranslation,
            NextWord = nextWord
        };
    }
}
