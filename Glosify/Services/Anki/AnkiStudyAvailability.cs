using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

// Shared observational eligibility for study selection and assistant suggestions.
internal sealed record AnkiStudyAvailability(List<AnkiCard> Cards, int NewStudied, int ReviewsStudied, int StudiedToday)
{
    public static async Task<AnkiStudyAvailability> ReadAsync(GlosifyContext db, AnkiCollection collection,
        DateTimeOffset now, CancellationToken cancellationToken)
    {
        var dayStart = AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var reviewQuery = db.AnkiReviews.AsNoTracking()
            .Where(review => review.AnkiCollectionId == collection.Id)
            .Select(review => new { review.AnkiCardId, review.Card.AnkiNoteId, review.PreviousState, review.ReviewedAt });
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
            reviewQuery = reviewQuery.Where(review => review.ReviewedAt >= dayStart);
        var reviews = await reviewQuery
            .ToListAsync(cancellationToken);
        // Filter after the indexed collection query so relational providers without native
        // DateTimeOffset ordering (notably SQLite in tests) preserve collection-day behavior.
        var reviewedToday = reviews.Where(review => review.ReviewedAt >= dayStart).ToList();
        var reviewedCardIds = reviewedToday.Select(review => review.AnkiCardId).ToHashSet();
        var reviewedNoteIds = reviewedToday.Select(review => review.AnkiNoteId).ToHashSet();
        var newStudied = reviewedToday.Count(review => review.PreviousState == AnkiCardStates.New);
        var reviewsStudied = reviewedToday.Count(review => review.PreviousState == AnkiCardStates.Review);

        var cards = await db.AnkiCards
            .AsNoTracking()
            .Include(card => card.Note)
            .Where(card => card.Note.AnkiCollectionId == collection.Id
                && card.IsActive)
            .ToListAsync(cancellationToken);
        cards = cards
            .Where(card => !card.BuriedUntil.HasValue || card.BuriedUntil <= now)
            .Where(card => !reviewedNoteIds.Contains(card.AnkiNoteId) || reviewedCardIds.Contains(card.Id))
            .ToList();

        return new(cards, newStudied, reviewsStudied, reviewedToday.Count);
    }
}
