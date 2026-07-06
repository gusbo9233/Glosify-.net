namespace Glosify.Models.Api;

public sealed record ExploreIndexDto(
    IReadOnlyList<ExploreCollectionCardDto> Collections,
    IReadOnlyList<ExploreQuizCardDto> Quizzes);

public sealed record ExploreCollectionCardDto(
    Guid Id, string Name, string Language, int CollectionCount, int QuizCount);

public sealed record ExploreQuizCardDto(
    Guid Id, string Name, string SourceLanguage, string TargetLanguage, int WordCount);

public sealed record ExploreCollectionDto(
    Guid Id,
    string Name,
    string Language,
    IReadOnlyList<ExploreQuizCardDto> Quizzes,
    IReadOnlyList<ExploreCollectionDto> ChildCollections);

public sealed record ExploreQuizDetailDto(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    IReadOnlyList<WordDto> Words,
    IReadOnlyList<SentenceDto> Sentences);
