using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed class AnkiStudyService : IAnkiStudyService
{
    private readonly GlosifyContext _context;
    private readonly IAnkiCollectionService _collections;
    private readonly IAnkiScheduler _scheduler;
    private readonly TimeProvider _timeProvider;

    public AnkiStudyService(
        GlosifyContext context,
        IAnkiCollectionService collections,
        IAnkiScheduler scheduler,
        TimeProvider timeProvider)
    {
        _context = context;
        _collections = collections;
        _scheduler = scheduler;
        _timeProvider = timeProvider;
    }

    public async Task<AnkiStudyState?> GetNextAsync(
        Guid collectionId,
        string userId,
        Guid? preferredCardId = null,
        CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == collectionId && item.UserId == userId,
            cancellationToken);
        if (collection is null)
            return null;
        await _collections.SyncCollectionAsync(collectionId, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var dayStart = AnkiCollectionService.StartOfCollectionDay(collection.TimeZoneId, now);
        var reviewQuery = _context.AnkiReviews.AsNoTracking()
            .Where(review => review.AnkiCollectionId == collectionId)
            .Select(review => new { review.AnkiCardId, review.Card.AnkiNoteId, review.PreviousState, review.ReviewedAt });
        if (_context.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) != true)
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

        var cards = await _context.AnkiCards
            .AsNoTracking()
            .Include(card => card.Note)
            .Where(card => card.Note.AnkiCollectionId == collectionId
                && card.IsActive)
            .ToListAsync(cancellationToken);
        cards = cards
            .Where(card => !card.BuriedUntil.HasValue || card.BuriedUntil <= now)
            .Where(card => !reviewedNoteIds.Contains(card.AnkiNoteId) || reviewedCardIds.Contains(card.Id))
            .ToList();

        AnkiCard? selected = preferredCardId.HasValue
            ? cards.SingleOrDefault(card => card.Id == preferredCardId.Value)
            : null;
        if (selected is null && !preferredCardId.HasValue)
        {
            selected = cards
                .Where(card => (card.State == AnkiCardStates.Learning || card.State == AnkiCardStates.Relearning)
                    && card.DueAt <= now)
                .OrderBy(card => card.DueAt)
                .FirstOrDefault();
        }
        if (selected is null && !preferredCardId.HasValue && reviewsStudied < collection.MaximumReviewsPerDay)
        {
            selected = cards
                .Where(card => card.State == AnkiCardStates.Review && card.DueAt <= now)
                .OrderBy(card => _scheduler.Retrievability(card, now))
                .ThenBy(card => card.DueAt)
                .FirstOrDefault();
        }
        if (selected is null && !preferredCardId.HasValue && newStudied < collection.NewCardsPerDay)
        {
            selected = cards
                .Where(card => card.State == AnkiCardStates.New)
                .OrderBy(card => card.Note.CreatedAt)
                .ThenBy(card => card.Id)
                .FirstOrDefault();
        }

        if (selected is null)
        {
            var nextDue = cards
                .Where(card => card.State != AnkiCardStates.New && card.DueAt > now)
                .MinBy(card => card.DueAt)?.DueAt;
            return new AnkiStudyState(null, nextDue);
        }

        var sourceToTarget = PracticeDirection.IsSourceToTarget(selected.Direction);
        var previews = _scheduler.Preview(selected, collection.DesiredRetention, now);
        var dueRemaining = cards.Count(card => card.State != AnkiCardStates.New && card.DueAt <= now);
        var newRemaining = Math.Max(0, Math.Min(
            collection.NewCardsPerDay - newStudied,
            cards.Count(card => card.State == AnkiCardStates.New)));
        return new AnkiStudyState(new AnkiStudyCard(
            collection.Id,
            collection.Name,
            selected.Id,
            selected.RowVersion is not { Length: > 0 } ? string.Empty : Convert.ToBase64String(selected.RowVersion),
            sourceToTarget ? selected.Note.SourceText : selected.Note.TargetText,
            sourceToTarget ? selected.Note.TargetText : selected.Note.SourceText,
            sourceToTarget ? collection.SourceLanguage : collection.TargetLanguage,
            sourceToTarget ? collection.TargetLanguage : collection.SourceLanguage,
            selected.Note.ItemType,
            selected.Direction,
            selected.State,
            previews.ToDictionary(pair => pair.Key, pair => pair.Value.IntervalLabel, StringComparer.Ordinal),
            dueRemaining,
            newRemaining), null);
    }

    public async Task<bool> RateAsync(
        RateAnkiCardInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var normalizedRating = input.Rating.Trim().ToLowerInvariant();
        _ = AnkiRatings.Grade(normalizedRating);
        if (await _context.AnkiReviews.AnyAsync(review => review.ClientToken == input.ClientToken, cancellationToken))
            return false;

        var card = await _context.AnkiCards
            .Include(item => item.Note).ThenInclude(note => note.Collection)
            .Include(item => item.Note).ThenInclude(note => note.Cards)
            .SingleOrDefaultAsync(item => item.Id == input.CardId
                && item.Note.AnkiCollectionId == input.CollectionId
                && item.Note.Collection.UserId == userId
                && item.IsActive,
                cancellationToken);
        if (card is null)
            return false;

        if (!string.IsNullOrWhiteSpace(input.RowVersion))
        {
            byte[] expected;
            try
            {
                expected = Convert.FromBase64String(input.RowVersion);
            }
            catch (FormatException)
            {
                throw new AnkiReviewConflictException();
            }
            if (card.RowVersion is { Length: > 0 } && !card.RowVersion.SequenceEqual(expected))
                throw new AnkiReviewConflictException();
            if (expected.Length > 0)
                _context.Entry(card).Property(item => item.RowVersion).OriginalValue = expected;
        }

        var now = _timeProvider.GetUtcNow();
        var collection = card.Note.Collection;
        var sourceToTarget = PracticeDirection.IsSourceToTarget(card.Direction);
        var prompt = sourceToTarget ? card.Note.SourceText : card.Note.TargetText;
        var answer = sourceToTarget ? card.Note.TargetText : card.Note.SourceText;
        var previousState = card.State;
        var previousDueAt = card.DueAt;
        var previousStability = card.Stability;
        var previousDifficulty = card.Difficulty;
        var scheduled = _scheduler.Schedule(card, normalizedRating, collection.DesiredRetention, now);

        card.State = scheduled.State;
        card.DueAt = scheduled.DueAt;
        card.Stability = scheduled.Stability;
        card.Difficulty = scheduled.Difficulty;
        card.LearningStep = scheduled.LearningStep;
        card.ScheduledDays = scheduled.ScheduledDays;
        card.ReviewCount = scheduled.ReviewCount;
        card.LapseCount = scheduled.LapseCount;
        card.LastReviewedAt = now;
        var nextCollectionDay = AnkiCollectionService.StartOfNextCollectionDay(collection.TimeZoneId, now);
        foreach (var sibling in card.Note.Cards.Where(sibling => sibling.Id != card.Id && sibling.IsActive))
            sibling.BuriedUntil = nextCollectionDay;
        _context.AnkiReviews.Add(new AnkiReview
        {
            Id = Guid.NewGuid(),
            AnkiCollectionId = collection.Id,
            AnkiCardId = card.Id,
            ClientToken = input.ClientToken,
            Rating = normalizedRating,
            PreviousState = previousState,
            NewState = scheduled.State,
            PreviousDueAt = previousDueAt,
            NewDueAt = scheduled.DueAt,
            ScheduledDays = scheduled.ScheduledDays,
            ElapsedDays = scheduled.ElapsedDays,
            PreviousStability = previousStability,
            NewStability = scheduled.Stability,
            PreviousDifficulty = previousDifficulty,
            NewDifficulty = scheduled.Difficulty,
            Retrievability = scheduled.Retrievability,
            Prompt = prompt,
            Answer = answer,
            DurationMilliseconds = input.DurationMilliseconds.HasValue
                ? Math.Clamp(input.DurationMilliseconds.Value, 0, 3_600_000)
                : null,
            SchedulerVersion = _scheduler.Version,
            ReviewedAt = now,
        });

        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new AnkiReviewConflictException();
        }
        catch (DbUpdateException)
        {
            _context.ChangeTracker.Clear();
            if (await _context.AnkiReviews.AsNoTracking().AnyAsync(
                    review => review.ClientToken == input.ClientToken, cancellationToken))
                return false;
            throw;
        }
        return true;
    }
}
