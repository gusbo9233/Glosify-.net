using Glosify.Models.Entities;

namespace Glosify.Services.Anki;

public sealed record AnkiStudyCard(
    Guid CollectionId,
    string CollectionName,
    Guid CardId,
    string RowVersion,
    string Prompt,
    string Answer,
    string PromptLanguage,
    string AnswerLanguage,
    string ItemType,
    string Direction,
    string State,
    IReadOnlyDictionary<string, string> Intervals,
    int DueRemaining,
    int NewRemaining);

public sealed record AnkiStudyState(AnkiStudyCard? Card, DateTimeOffset? NextDueAt, string TimeZoneId);

public sealed record RateAnkiCardInput(
    Guid CollectionId,
    Guid CardId,
    string Rating,
    Guid ClientToken,
    string RowVersion,
    int? DurationMilliseconds);

public interface IAnkiStudyService
{
    Task<AnkiStudyState?> GetNextAsync(
        Guid collectionId,
        string userId,
        Guid? preferredCardId = null,
        CancellationToken cancellationToken = default);
    Task<bool> RateAsync(RateAnkiCardInput input, string userId, CancellationToken cancellationToken = default);
}

public sealed class AnkiReviewConflictException : InvalidOperationException
{
    public AnkiReviewConflictException()
        : base("This card changed in another tab. Reload the session and try again.")
    {
    }
}
