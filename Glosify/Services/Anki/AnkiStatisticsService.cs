using Glosify.Data;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed class AnkiStatisticsService : IAnkiStatisticsService
{
    private readonly GlosifyContext _context;
    private readonly IAnkiCollectionService _collections;
    private readonly TimeProvider _timeProvider;

    public AnkiStatisticsService(
        GlosifyContext context,
        IAnkiCollectionService collections,
        TimeProvider timeProvider)
    {
        _context = context;
        _collections = collections;
        _timeProvider = timeProvider;
    }

    public async Task<AnkiStatistics?> GetAsync(
        Guid collectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == collectionId && item.UserId == userId,
            cancellationToken);
        if (collection is null)
            return null;
        await _collections.SyncCollectionAsync(collectionId, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var zone = FindTimeZone(collection.TimeZoneId);
        var localToday = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).Date);
        var periodStartLocal = localToday.AddDays(-29);
        var periodStart = LocalStart(periodStartLocal, zone);
        var reviewQuery = _context.AnkiReviews.AsNoTracking()
            .Where(review => review.AnkiCollectionId == collectionId);
        var reviews = IsSqlite()
            ? (await reviewQuery.ToListAsync(cancellationToken)).Where(review => review.ReviewedAt >= periodStart).ToList()
            : await reviewQuery.Where(review => review.ReviewedAt >= periodStart).ToListAsync(cancellationToken);
        var groupedReviews = reviews.GroupBy(review =>
            DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(review.ReviewedAt, zone).Date))
            .ToDictionary(group => group.Key, group => group.Count());
        var activity = Enumerable.Range(0, 30)
            .Select(offset => periodStartLocal.AddDays(offset))
            .Select(day => new AnkiChartPoint(day.ToString("MMM d"), groupedReviews.GetValueOrDefault(day)))
            .ToList();

        var forecastEnd = LocalStart(localToday.AddDays(14), zone);
        var dueQuery = _context.AnkiCards.AsNoTracking()
            .Where(card => card.Note.AnkiCollectionId == collectionId
                && card.IsActive
                && card.State != AnkiCardStates.New);
        var dueCards = IsSqlite()
            ? (await dueQuery.Select(card => card.DueAt).ToListAsync(cancellationToken)).Where(due => due < forecastEnd).ToList()
            : await dueQuery.Where(card => card.DueAt < forecastEnd).Select(card => card.DueAt).ToListAsync(cancellationToken);
        var groupedDue = dueCards
            .Where(due => due.HasValue)
            .GroupBy(due => DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(due!.Value, zone).Date))
            .ToDictionary(group => group.Key, group => group.Count());
        var forecast = Enumerable.Range(0, 14)
            .Select(offset => localToday.AddDays(offset))
            .Select(day => new AnkiChartPoint(day.ToString("MMM d"), groupedDue.GetValueOrDefault(day)))
            .ToList();

        var activeCards = await _context.AnkiCards.AsNoTracking()
            .Where(card => card.Note.AnkiCollectionId == collectionId && card.IsActive)
            .ToListAsync(cancellationToken);
        var dayStart = AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var counts = new AnkiCollectionCounts(
            activeCards.Count(card => card.State != AnkiCardStates.New && card.DueAt <= now),
            activeCards.Count(card => card.State == AnkiCardStates.New),
            activeCards.Count(card => card.State is AnkiCardStates.Learning or AnkiCardStates.Relearning),
            activeCards.Count,
            reviews.Count(review => review.ReviewedAt >= dayStart));
        var passed = reviews.Count(review => review.Rating != AnkiRatings.Again);
        var retention = reviews.Count == 0 ? 0 : passed * 100d / reviews.Count;
        return new AnkiStatistics(counts, reviews.Count, retention, activity, forecast);
    }

    private static DateTimeOffset LocalStart(DateOnly date, TimeZoneInfo zone)
    {
        var local = DateTime.SpecifyKind(date.ToDateTime(TimeOnly.MinValue), DateTimeKind.Unspecified);
        return new DateTimeOffset(local, zone.GetUtcOffset(local)).ToUniversalTime();
    }

    private bool IsSqlite() => _context.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true;

    private static TimeZoneInfo FindTimeZone(string id)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(id);
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.Utc;
        }
        catch (InvalidTimeZoneException)
        {
            return TimeZoneInfo.Utc;
        }
    }
}
