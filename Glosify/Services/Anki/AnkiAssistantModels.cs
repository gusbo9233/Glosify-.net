using System.Text.Json;
using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

public sealed record AnkiPage<T>(IReadOnlyList<T> Items, int TotalCount, int? NextOffset);
public sealed record AnkiInspection(AnkiCollectionSummary Collection, AnkiPage<AnkiCardListItem> Cards, AnkiPage<AnkiLinkState> Links);
public sealed record AddAnkiItemsInput(Guid CollectionId, Guid QuizId, string ItemType, IReadOnlyList<string> ItemIds, bool SourceToTarget, bool TargetToSource);
public sealed record AnkiAdditionResult(Guid CollectionId, int SelectedItems, int CardsAdded, int AlreadyIncluded,
    IReadOnlyList<AnkiCardChange> Cards, AnkiLinkChange? Link = null, int ExcludedCards = 0);
public sealed record AnkiCardChange(AnkiCardState? Before, AnkiCardState After);
public sealed record AnkiLinkChange(AnkiLinkSnapshot Before, AnkiLinkSnapshot After);
public sealed record AnkiLinkSnapshot(Guid CollectionId, Guid QuizId, AnkiLinkState? Link, IReadOnlyList<AnkiCardState> Cards);
public sealed record AnkiLinkState(Guid Id, Guid QuizId, bool WordsSourceToTarget, bool WordsTargetToSource,
    bool SentencesSourceToTarget, bool SentencesTargetToSource, DateTimeOffset UpdatedAt, DateTimeOffset CreatedAt = default)
{
    public static AnkiLinkState From(AnkiQuizLink link) => new(link.Id, link.QuizId, link.WordsSourceToTarget,
        link.WordsTargetToSource, link.SentencesSourceToTarget, link.SentencesTargetToSource, link.UpdatedAt, link.CreatedAt);
}

// Membership can be restored without overwriting scheduling. The fingerprint fences later edits/reviews.
// Compare semantic state, not RowVersion: undoing a later change from the same run advances the
// database token even when it restores exactly the state an earlier journal entry expects.
public sealed record AnkiCardState(Guid Id, Guid NoteId, bool DirectlyIncluded, bool QuizLinkIncluded,
    bool ExcludedFromQuizLink, bool IsActive, string Fingerprint)
{
    public static AnkiCardState From(AnkiCard card) => new(card.Id, card.AnkiNoteId, card.DirectlyIncluded,
        card.QuizLinkIncluded, card.ExcludedFromQuizLink, card.IsActive, JsonSerializer.Serialize(new
        {
            card.Direction, card.State, card.DueAt, card.Stability, card.Difficulty, card.LearningStep,
            card.ReviewCount, card.LapseCount, card.ScheduledDays, card.LastReviewedAt, card.BuriedUntil,
            card.Note.WordId, card.Note.SentenceId, card.Note.QuizId,
            card.Note.TargetText, card.Note.SourceText,
        }));
}

public sealed record AnkiCollectionState(Guid Id, string Fingerprint)
{
    public static AnkiCollectionState From(AnkiCollection collection) => new(collection.Id, JsonSerializer.Serialize(new
    {
        collection.Name, collection.SourceLanguage, collection.TargetLanguage, collection.DefaultDirection,
        collection.DesiredRetention, collection.NewCardsPerDay, collection.MaximumReviewsPerDay,
        collection.TimeZoneId, collection.UpdatedAt,
    }));
}
