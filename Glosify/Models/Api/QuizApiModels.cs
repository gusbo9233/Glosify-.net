namespace Glosify.Models.Api;

public sealed record QuizSummaryDto(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string Language,
    Guid? CollectionId,
    DateTimeOffset CreatedAt)
{
    public static QuizSummaryDto From(Quiz quiz) => new(
        quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
        quiz.Language, quiz.CollectionId, quiz.CreatedAt);
}

public sealed record QuizDetailDto(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string Language,
    Guid? CollectionId,
    DateTimeOffset CreatedAt,
    int WordCount,
    int SentenceCount,
    bool IsPublic)
{
    public static QuizDetailDto From(Quiz quiz, int wordCount, int sentenceCount) => new(
        quiz.Id, quiz.Name, quiz.SourceLanguage, quiz.TargetLanguage,
        quiz.Language, quiz.CollectionId, quiz.CreatedAt, wordCount, sentenceCount, quiz.IsPublic);
}

public sealed record WordDto(string Id, string Lemma, string Translation, DateTimeOffset CreatedAt);

public sealed record SentenceDto(Guid Id, string Text, string Translation, int WordCount);

public sealed record SetVisibilityRequest(bool IsPublic);

public sealed record RepairSentenceRequest(string Text);

public sealed record RepairResultDto(string Message);

public sealed record ExtractedTextDto(string Text);

public sealed record CreateQuizRequest(string Name, string SourceLanguage, string TargetLanguage, Guid? CollectionId);

public sealed record AddWordRequest(string Word, string Translation);
