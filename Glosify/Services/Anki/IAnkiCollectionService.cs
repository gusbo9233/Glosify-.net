using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

public sealed record AnkiCollectionCounts(int Due, int New, int Learning, int Total, int StudiedToday);

public sealed record AnkiCollectionSummary(
    Guid Id,
    string Name,
    string TargetLanguage,
    string DefaultDirection,
    AnkiCollectionCounts Counts);

// Everything the single Anki page needs for one learning language. Collection is
// null until the user first adds content; quizzes and items are still listed.
public sealed record AnkiWorkspace(
    AnkiCollection? Collection,
    IReadOnlyList<AnkiQuizLink> QuizLinks,
    IReadOnlyList<Quiz> CompatibleQuizzes,
    IReadOnlyList<AnkiAvailableItem> AvailableItems);

public sealed record AnkiAvailableItem(
    Guid QuizId,
    string QuizName,
    string SourceLanguage,
    string ItemType,
    string ItemId,
    string TargetText,
    string SourceText,
    bool InAnki);

public sealed record AnkiItemKey(Guid QuizId, string ItemType, string ItemId);

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
    Task<IReadOnlyList<AnkiCardState>> ReadCardStatesAsync(Guid collectionId, IReadOnlyList<Guid> cardIds, string userId, CancellationToken cancellationToken = default);
    Task<AnkiLinkSnapshot> ReadLinkStateAsync(Guid collectionId, Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnkiCardChange>> RemoveCardsWithUndoAsync(RemoveAnkiCardsInput input, string userId, CancellationToken cancellationToken = default);
    Task<AnkiLinkChange> UnlinkWithUndoAsync(AnkiLinkSnapshot expected, string userId, CancellationToken cancellationToken = default);
    Task<AnkiCollectionCounts?> ReadCountsAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
    Task<AnkiPage<AnkiCollectionSummary>> BrowseAsync(string userId, int offset, CancellationToken cancellationToken = default);
    Task<AnkiInspection?> InspectAsync(Guid collectionId, string userId, int cardOffset, int linkOffset, CancellationToken cancellationToken = default);
    Task<AnkiAdditionResult> AddItemsAsync(AddAnkiItemsInput input, string userId, CancellationToken cancellationToken = default);
    Task<AnkiAdditionResult> LinkQuizAdditiveAsync(AddAnkiQuizInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> UndoCardAdditionAsync(AnkiCardChange change, string userId, CancellationToken cancellationToken = default);
    Task<bool> UndoQuizLinkAsync(AnkiLinkChange change, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AnkiCollectionSummary>> ListAsync(string userId, CancellationToken cancellationToken = default);
    Task<AnkiCollection?> FindForLanguageAsync(string userId, string targetLanguage, CancellationToken cancellationToken = default);
    Task<AnkiCollection> GetOrCreateAsync(string userId, string targetLanguage, string? timeZoneId, CancellationToken cancellationToken = default);
    Task<bool> IsOwnedByLanguageAsync(Guid collectionId, string targetLanguage, string userId, CancellationToken cancellationToken = default);
    Task<AnkiWorkspace> GetWorkspaceAsync(string userId, string targetLanguage, CancellationToken cancellationToken = default);
    Task<bool> SetPracticeDirectionAsync(Guid collectionId, string direction, string userId, CancellationToken cancellationToken = default);
    Task<bool> UpdateTimeZoneAsync(Guid collectionId, string timeZoneId, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddQuizForLanguageAsync(string targetLanguage, string? timeZoneId, Guid quizId, bool includeSentences, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddItemForLanguageAsync(string targetLanguage, string? timeZoneId, AnkiItemKey item, string userId, CancellationToken cancellationToken = default);
    Task<bool> SetMembershipForLanguageAsync(string targetLanguage, string? timeZoneId, IReadOnlyList<AnkiItemKey> add, IReadOnlyList<AnkiItemKey> remove, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddQuizAsync(AddAnkiQuizInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveQuizAsync(Guid collectionId, Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<bool> AddItemAsync(AddAnkiItemInput input, string userId, CancellationToken cancellationToken = default);
    Task<bool> SetMembershipAsync(Guid collectionId, IReadOnlyList<AnkiItemKey> add, IReadOnlyList<AnkiItemKey> remove, string userId, CancellationToken cancellationToken = default);
    Task<bool> RemoveCardAsync(Guid cardId, string userId, CancellationToken cancellationToken = default);
    Task SyncCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default);
    Task SyncQuizAsync(Guid quizId, CancellationToken cancellationToken = default);
    Task RetireQuizAsync(Guid quizId, CancellationToken cancellationToken = default);
}

public sealed class AnkiValidationException : InvalidOperationException
{
    public AnkiValidationException(string message) : base(message) { }
}
