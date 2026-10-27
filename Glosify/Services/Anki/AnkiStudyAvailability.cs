using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

// Shared observational eligibility for study selection and assistant suggestions.
// Each direction is its own track: a direction filter limits both the cards and the
// study statistics, and a review in one direction never hides the other.
internal sealed record AnkiStudyAvailability(List<AnkiCard> Cards, int StudiedToday)
{
    public static async Task<AnkiStudyAvailability> ReadAsync(GlosifyContext db, AnkiCollection collection,
        DateTimeOffset now, string? direction, CancellationToken cancellationToken)
    {
        var dayStart = AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var reviewQuery = db.AnkiReviews.AsNoTracking()
            .Where(review => review.AnkiCollectionId == collection.Id
                && (direction == null || review.Card.Direction == direction))
            .Select(review => new { review.ReviewedAt });
        if (db.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
            reviewQuery = reviewQuery.Where(review => review.ReviewedAt >= dayStart);
        var reviews = await reviewQuery
            .ToListAsync(cancellationToken);
        // Filter after the indexed collection query so relational providers without native
        // DateTimeOffset ordering (notably SQLite in tests) preserve collection-day behavior.
        var reviewedToday = reviews.Where(review => review.ReviewedAt >= dayStart).ToList();

        var cards = await db.AnkiCards
            .AsNoTracking()
            .Include(card => card.Note)
            .Where(card => card.Note.AnkiCollectionId == collection.Id
                && card.IsActive
                && (direction == null || card.Direction == direction))
            .ToListAsync(cancellationToken);
        cards = cards
            .Where(card => !card.BuriedUntil.HasValue || card.BuriedUntil <= now)
            .ToList();

        return new(cards, reviewedToday.Count);
    }
}
