using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

public sealed record AnkiCollectionCounts(int Due, int New, int Learning, int Total, int StudiedToday);

public sealed record AnkiCollectionSummary(
    Guid Id,
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string DefaultDirection,
    AnkiCollectionCounts Counts);

public sealed record AnkiCollectionDetails(
    AnkiCollection Collection,
    IReadOnlyList<AnkiQuizLink> QuizLinks,
    IReadOnlyList<AnkiCardListItem> Cards,
    IReadOnlyList<Quiz> CompatibleQuizzes,
    IReadOnlyList<AnkiAvailableItem> AvailableItems,
    AnkiCollectionCounts Counts);

public sealed record AnkiAvailableItem(Guid QuizId, string QuizName, string ItemType, string ItemId, string TargetText, string SourceText);

public sealed record AnkiCardListItem(
    Guid CardId,
    Guid NoteId,
    Guid QuizId,
    string ItemType,
    string TargetText,
    string SourceText,
    string Direction,
    string State,
    DateTimeOffset? DueAt,
    bool DirectlyIncluded,
    bool QuizLinkIncluded);

public sealed record CreateAnkiCollectionInput(
    string Name,
    string SourceLanguage,
    string TargetLanguage,
    string TimeZoneId);

public sealed record AddAnkiQuizInput(
    Guid CollectionId,
    Guid QuizId,
    bool WordsSourceToTarget,
    bool WordsTargetToSource,
    bool SentencesSourceToTarget,
    bool SentencesTargetToSource);

public sealed record AddAnkiItemInput(
    Guid CollectionId,
    Guid QuizId,
    string ItemType,
    string ItemId,
    bool SourceToTarget,
    bool TargetToSource);

public interface IAnkiCollectionService
{
    Task<IReadOnlyList<AnkiCollectionSummary>> ListAsync(string userId, CancellationToken cancellationToken = default);
    Task<AnkiCollectionDetails?> GetDetailsAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
    Task<AnkiCollection> CreateAsync(CreateAnkiCollectionInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> RenameAsync(Guid collectionId, string name, string userId, CancellationToken cancellationToken = default);
    Task<bool> UpdateSettingsAsync(Guid collectionId, double desiredRetention, int newCardsPerDay, int maximumReviewsPerDay, string timeZoneId, string userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddQuizAsync(AddAnkiQuizInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveQuizAsync(Guid collectionId, Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddItemAsync(AddAnkiItemInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveCardAsync(Guid cardId, string userId, CancellationToken cancellationToken = default);
    Task SyncCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task SyncQuizAsync(Guid quizId, CancellationToken cancellationToken = default);
    Task RetireQuizAsync(Guid quizId, CancellationToken cancellationToken = default);
}
