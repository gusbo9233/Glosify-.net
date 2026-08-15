using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed class AnkiCollectionService : IAnkiCollectionService
{
    private readonly GlosifyContext _context;
    private readonly TimeProvider _timeProvider;

    public AnkiCollectionService(GlosifyContext context, TimeProvider timeProvider)
    {
        _context = context;
        _timeProvider = timeProvider;
    }

    public async Task<IReadOnlyList<AnkiCollectionSummary>> ListAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        var ids = await _context.AnkiCollections
            .AsNoTracking()
            .Where(collection => collection.UserId == userId)
            .OrderBy(collection => collection.Name)
            .Select(collection => collection.Id)
            .ToListAsync(cancellationToken);
        foreach (var id in ids)
            await SyncCollectionAsync(id, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var collections = await _context.AnkiCollections
            .AsNoTracking()
            .Where(collection => collection.UserId == userId)
            .OrderBy(collection => collection.Name)
            .ToListAsync(cancellationToken);
        var summaries = new List<AnkiCollectionSummary>(collections.Count);
        foreach (var collection in collections)
        {
            summaries.Add(new AnkiCollectionSummary(
                collection.Id,
                collection.Name,
                collection.SourceLanguage,
                collection.TargetLanguage,
                collection.DefaultDirection,
                await CountsAsync(collection, now, cancellationToken)));
        }
        return summaries;
    }

    public async Task<AnkiCollectionDetails?> GetDetailsAsync(
        Guid collectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var owned = await _context.AnkiCollections
            .AsNoTracking()
            .AnyAsync(collection => collection.Id == collectionId && collection.UserId == userId, cancellationToken);
        if (!owned)
            return null;

        await SyncCollectionAsync(collectionId, cancellationToken);
        var collection = await _context.AnkiCollections
            .AsNoTracking()
            .SingleAsync(item => item.Id == collectionId, cancellationToken);
        var links = await _context.AnkiQuizLinks
            .AsNoTracking()
            .Include(link => link.Quiz)
            .Where(link => link.AnkiCollectionId == collectionId)
            .OrderBy(link => link.Quiz.Name)
            .ToListAsync(cancellationToken);
        var cards = await _context.AnkiCards
            .AsNoTracking()
            .Include(card => card.Note)
            .Where(card => card.Note.AnkiCollectionId == collectionId && card.IsActive)
            .OrderBy(card => card.Note.TargetText)
            .ThenBy(card => card.Direction)
            .Select(card => new AnkiCardListItem(
                card.Id,
                card.Note.Id,
                card.Note.QuizId,
                card.Note.ItemType,
                card.Note.TargetText,
                card.Note.SourceText,
                card.Direction,
                card.State,
                card.DueAt,
                card.DirectlyIncluded,
                card.QuizLinkIncluded))
            .ToListAsync(cancellationToken);
        var quizzes = await _context.Quizzes
            .AsNoTracking()
            .Where(quiz => quiz.UserId == userId
                && quiz.SourceLanguage == collection.SourceLanguage
                && quiz.TargetLanguage == collection.TargetLanguage)
            .OrderBy(quiz => quiz.Name)
            .ToListAsync(cancellationToken);
        var quizIds = quizzes.Select(quiz => quiz.Id).ToList();
        var quizNames = quizzes.ToDictionary(quiz => quiz.Id, quiz => quiz.Name);
        var availableItems = (await _context.Words.AsNoTracking()
                .Where(word => quizIds.Contains(word.QuizId))
                .OrderBy(word => word.CreatedAt)
                .Select(word => new { word.QuizId, ItemId = word.Id, TargetText = word.Lemma, SourceText = word.Translation })
                .ToListAsync(cancellationToken))
            .Select(word => new AnkiAvailableItem(word.QuizId, quizNames[word.QuizId], "word", word.ItemId, word.TargetText, word.SourceText))
            .Concat((await _context.QuizSentences.AsNoTracking()
                .Where(sentence => quizIds.Contains(sentence.QuizId))
                .OrderBy(sentence => sentence.CreatedAt)
                .Select(sentence => new { sentence.QuizId, ItemId = sentence.Id, TargetText = sentence.Text, SourceText = sentence.Translation })
                .ToListAsync(cancellationToken))
                .Select(sentence => new AnkiAvailableItem(sentence.QuizId, quizNames[sentence.QuizId], "sentence", sentence.ItemId.ToString(), sentence.TargetText, sentence.SourceText)))
            .ToList();

        return new AnkiCollectionDetails(
            collection,
            links,
            cards,
            quizzes,
            availableItems,
            await CountsAsync(collection, _timeProvider.GetUtcNow(), cancellationToken));
    }

    public async Task<AnkiCollection> CreateAsync(
        CreateAnkiCollectionInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var name = Required(input.Name, "Enter a collection name.", 160);
        var source = Required(input.SourceLanguage, "Choose a source language.", 64);
        var target = Required(input.TargetLanguage, "Choose a target language.", 64);
        if (string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Source and target languages must be different.");
        if (await _context.AnkiCollections.AnyAsync(
            collection => collection.UserId == userId && collection.Name == name,
            cancellationToken))
        {
            throw new InvalidOperationException("An Anki collection with that name already exists.");
        }

        var now = _timeProvider.GetUtcNow();
        var collection = new AnkiCollection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = name,
            SourceLanguage = source,
            TargetLanguage = target,
            TimeZoneId = NormalizeTimeZone(input.TimeZoneId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.AnkiCollections.Add(collection);
        await _context.SaveChangesAsync(cancellationToken);
        return collection;
    }

    public async Task<bool> RenameAsync(
        Guid collectionId,
        string name,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var collection = await OwnedCollectionAsync(collectionId, userId, cancellationToken);
        if (collection is null)
            return false;
        var normalized = Required(name, "Enter a collection name.", 160);
        if (await _context.AnkiCollections.AnyAsync(
            candidate => candidate.UserId == userId && candidate.Id != collectionId && candidate.Name == normalized,
            cancellationToken))
        {
            throw new InvalidOperationException("An Anki collection with that name already exists.");
        }
        collection.Name = normalized;
        collection.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> UpdateSettingsAsync(
        Guid collectionId,
        double desiredRetention,
        int newCardsPerDay,
        int maximumReviewsPerDay,
        string timeZoneId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var collection = await OwnedCollectionAsync(collectionId, userId, cancellationToken);
        if (collection is null)
            return false;
        collection.DesiredRetention = Math.Clamp(desiredRetention, 0.70, 0.97);
        collection.NewCardsPerDay = Math.Clamp(newCardsPerDay, 0, 999);
        collection.MaximumReviewsPerDay = Math.Clamp(maximumReviewsPerDay, 1, 9_999);
        collection.TimeZoneId = NormalizeTimeZone(timeZoneId);
        collection.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> DeleteAsync(
        Guid collectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections
            .Include(item => item.Reviews)
            .Include(item => item.Notes).ThenInclude(note => note.Cards)
            .Include(item => item.QuizLinks)
            .SingleOrDefaultAsync(item => item.Id == collectionId && item.UserId == userId, cancellationToken);
        if (collection is null)
            return false;
        _context.AnkiReviews.RemoveRange(collection.Reviews);
        _context.AnkiCards.RemoveRange(collection.Notes.SelectMany(note => note.Cards));
        _context.AnkiNotes.RemoveRange(collection.Notes);
        _context.AnkiQuizLinks.RemoveRange(collection.QuizLinks);
        _context.AnkiCollections.Remove(collection);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> AddQuizAsync(
        AddAnkiQuizInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!input.WordsSourceToTarget && !input.WordsTargetToSource
            && !input.SentencesSourceToTarget && !input.SentencesTargetToSource)
        {
            throw new InvalidOperationException("Choose at least one content type and direction.");
        }

        var collection = await OwnedCollectionAsync(input.CollectionId, userId, cancellationToken);
        var quiz = await _context.Quizzes.SingleOrDefaultAsync(
            item => item.Id == input.QuizId && item.UserId == userId,
            cancellationToken);
        if (collection is null || quiz is null || !Matches(collection, quiz))
            return false;

        var now = _timeProvider.GetUtcNow();
        var link = await _context.AnkiQuizLinks.SingleOrDefaultAsync(
            item => item.AnkiCollectionId == input.CollectionId && item.QuizId == input.QuizId,
            cancellationToken);
        if (link is null)
        {
            link = new AnkiQuizLink
            {
                Id = Guid.NewGuid(),
                AnkiCollectionId = input.CollectionId,
                QuizId = input.QuizId,
                CreatedAt = now,
            };
            _context.AnkiQuizLinks.Add(link);
        }
        link.WordsSourceToTarget = input.WordsSourceToTarget;
        link.WordsTargetToSource = input.WordsTargetToSource;
        link.SentencesSourceToTarget = input.SentencesSourceToTarget;
        link.SentencesTargetToSource = input.SentencesTargetToSource;
        link.UpdatedAt = now;
        collection.DefaultDirection = input.WordsTargetToSource && !input.WordsSourceToTarget
            ? PracticeDirection.TargetToSource
            : PracticeDirection.SourceToTarget;
        collection.UpdatedAt = now;
        await _context.SaveChangesAsync(cancellationToken);
        await SyncCollectionAsync(input.CollectionId, cancellationToken);

        var linkedCards = await _context.AnkiCards
            .Include(card => card.Note)
            .Where(card => card.Note.AnkiCollectionId == input.CollectionId
                && card.Note.QuizId == input.QuizId
                && card.QuizLinkIncluded)
            .ToListAsync(cancellationToken);
        foreach (var card in linkedCards)
        {
            card.ExcludedFromQuizLink = false;
            card.IsActive = true;
        }
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveQuizAsync(
        Guid collectionId,
        Guid quizId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!await OwnsCollectionAsync(collectionId, userId, cancellationToken))
            return false;
        var link = await _context.AnkiQuizLinks.SingleOrDefaultAsync(
            item => item.AnkiCollectionId == collectionId && item.QuizId == quizId,
            cancellationToken);
        if (link is null)
            return false;
        _context.AnkiQuizLinks.Remove(link);
        await _context.SaveChangesAsync(cancellationToken);
        await SyncCollectionAsync(collectionId, cancellationToken);
        return true;
    }

    public async Task<bool> AddItemAsync(
        AddAnkiItemInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!input.SourceToTarget && !input.TargetToSource)
            throw new InvalidOperationException("Choose at least one direction.");
        var collection = await OwnedCollectionAsync(input.CollectionId, userId, cancellationToken);
        var quiz = await _context.Quizzes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == input.QuizId && item.UserId == userId,
            cancellationToken);
        if (collection is null || quiz is null || !Matches(collection, quiz))
            return false;

        await SyncCollectionAsync(input.CollectionId, cancellationToken);
        var itemType = PracticeItemType.Normalize(input.ItemType);
        string target;
        string source;
        string? wordId = null;
        Guid? sentenceId = null;
        if (PracticeItemType.IsSentences(itemType))
        {
            if (!Guid.TryParse(input.ItemId, out var parsed))
                return false;
            var sentence = await _context.QuizSentences.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == parsed && item.QuizId == input.QuizId,
                cancellationToken);
            if (sentence is null)
                return false;
            sentenceId = sentence.Id;
            target = sentence.Text.Trim();
            source = sentence.Translation.Trim();
        }
        else
        {
            var word = await _context.Words.AsNoTracking().SingleOrDefaultAsync(
                item => item.Id == input.ItemId && item.QuizId == input.QuizId,
                cancellationToken);
            if (word is null)
                return false;
            wordId = word.Id;
            target = word.Lemma.Trim();
            source = word.Translation.Trim();
        }

        var note = await FindNoteAsync(input.CollectionId, wordId, sentenceId, cancellationToken);
        if (note is null)
        {
            note = NewNote(input.CollectionId, input.QuizId, itemType, wordId, sentenceId, target, source);
            _context.AnkiNotes.Add(note);
        }
        else
        {
            note.TargetText = target;
            note.SourceText = source;
            note.IsActive = true;
            note.UpdatedAt = _timeProvider.GetUtcNow();
        }
        if (input.SourceToTarget)
            EnsureDirectCard(note, PracticeDirection.SourceToTarget);
        if (input.TargetToSource)
            EnsureDirectCard(note, PracticeDirection.TargetToSource);
        collection.DefaultDirection = input.TargetToSource && !input.SourceToTarget
            ? PracticeDirection.TargetToSource
            : PracticeDirection.SourceToTarget;
        collection.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task<bool> RemoveCardAsync(
        Guid cardId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var card = await _context.AnkiCards
            .Include(item => item.Note).ThenInclude(note => note.Collection)
            .Include(item => item.Note).ThenInclude(note => note.Cards)
            .SingleOrDefaultAsync(item => item.Id == cardId && item.Note.Collection.UserId == userId, cancellationToken);
        if (card is null)
            return false;
        card.DirectlyIncluded = false;
        card.ExcludedFromQuizLink = card.QuizLinkIncluded;
        card.IsActive = false;
        card.Note.IsActive = card.Note.Cards.Any(candidate => candidate.Id != card.Id && candidate.IsActive);
        await _context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SyncQuizAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        var collectionIds = await _context.AnkiQuizLinks
            .Where(link => link.QuizId == quizId)
            .Select(link => link.AnkiCollectionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        var directCollectionIds = await _context.AnkiNotes
            .Where(note => note.QuizId == quizId && note.Cards.Any(card => card.DirectlyIncluded))
            .Select(note => note.AnkiCollectionId)
            .Distinct()
            .ToListAsync(cancellationToken);
        foreach (var collectionId in collectionIds.Concat(directCollectionIds).Distinct())
            await SyncCollectionAsync(collectionId, cancellationToken);
    }

    public async Task RetireQuizAsync(Guid quizId, CancellationToken cancellationToken = default)
    {
        var links = await _context.AnkiQuizLinks.Where(link => link.QuizId == quizId).ToListAsync(cancellationToken);
        var notes = await _context.AnkiNotes
            .Include(note => note.Cards)
            .Where(note => note.QuizId == quizId)
            .ToListAsync(cancellationToken);
        _context.AnkiQuizLinks.RemoveRange(links);
        foreach (var note in notes)
        {
            note.IsActive = false;
            foreach (var card in note.Cards)
            {
                card.IsActive = false;
                card.QuizLinkIncluded = false;
                card.DirectlyIncluded = false;
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    public async Task SyncCollectionAsync(Guid collectionId, CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections
            .Include(item => item.QuizLinks)
            .SingleOrDefaultAsync(item => item.Id == collectionId, cancellationToken);
        if (collection is null)
            return;
        var notes = await _context.AnkiNotes
            .Include(note => note.Cards)
            .Where(note => note.AnkiCollectionId == collectionId)
            .ToListAsync(cancellationToken);
        var quizIds = collection.QuizLinks.Select(link => link.QuizId)
            .Concat(notes.Select(note => note.QuizId))
            .Distinct()
            .ToList();
        var quizzes = await _context.Quizzes.AsNoTracking()
            .Where(quiz => quizIds.Contains(quiz.Id))
            .ToDictionaryAsync(quiz => quiz.Id, cancellationToken);
        var words = await _context.Words.AsNoTracking()
            .Where(word => quizIds.Contains(word.QuizId))
            .ToListAsync(cancellationToken);
        var sentences = await _context.QuizSentences.AsNoTracking()
            .Where(sentence => quizIds.Contains(sentence.QuizId))
            .ToListAsync(cancellationToken);
        var wordsById = words.ToDictionary(word => word.Id, StringComparer.Ordinal);
        var sentencesById = sentences.ToDictionary(sentence => sentence.Id);

        foreach (var note in notes)
        {
            foreach (var card in note.Cards)
                card.QuizLinkIncluded = false;
            if (note.WordId is not null && wordsById.TryGetValue(note.WordId, out var word))
            {
                note.TargetText = word.Lemma.Trim();
                note.SourceText = word.Translation.Trim();
            }
            else if (note.SentenceId.HasValue && sentencesById.TryGetValue(note.SentenceId.Value, out var sentence))
            {
                note.TargetText = sentence.Text.Trim();
                note.SourceText = sentence.Translation.Trim();
            }
        }

        foreach (var link in collection.QuizLinks)
        {
            if (!quizzes.TryGetValue(link.QuizId, out var quiz) || !Matches(collection, quiz))
                continue;
            foreach (var word in words.Where(item => item.QuizId == link.QuizId))
            {
                var note = GetOrCreateNote(notes, collectionId, link.QuizId, PracticeItemType.Words, word.Id, null, word.Lemma, word.Translation);
                if (link.WordsSourceToTarget)
                    EnsureLinkedCard(note, PracticeDirection.SourceToTarget);
                if (link.WordsTargetToSource)
                    EnsureLinkedCard(note, PracticeDirection.TargetToSource);
            }
            foreach (var sentence in sentences.Where(item => item.QuizId == link.QuizId))
            {
                var note = GetOrCreateNote(notes, collectionId, link.QuizId, PracticeItemType.Sentences, null, sentence.Id, sentence.Text, sentence.Translation);
                if (link.SentencesSourceToTarget)
                    EnsureLinkedCard(note, PracticeDirection.SourceToTarget);
                if (link.SentencesTargetToSource)
                    EnsureLinkedCard(note, PracticeDirection.TargetToSource);
            }
        }

        foreach (var note in notes)
        {
            var sourceExists = note.WordId is not null
                ? wordsById.ContainsKey(note.WordId)
                : note.SentenceId.HasValue && sentencesById.ContainsKey(note.SentenceId.Value);
            foreach (var card in note.Cards)
                card.IsActive = sourceExists && (card.DirectlyIncluded || (card.QuizLinkIncluded && !card.ExcludedFromQuizLink));
            note.IsActive = note.Cards.Any(card => card.IsActive);
            note.UpdatedAt = _timeProvider.GetUtcNow();
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AnkiCollectionCounts> CountsAsync(
        AnkiCollection collection,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var dayStart = StartOfCollectionDay(collection.TimeZoneId, now);
        var query = _context.AnkiCards.AsNoTracking()
            .Where(card => card.Note.AnkiCollectionId == collection.Id && card.IsActive);
        var due = IsSqlite()
            ? (await query.ToListAsync(cancellationToken)).Count(card => card.State != AnkiCardStates.New && card.DueAt <= now)
            : await query.CountAsync(card => card.State != AnkiCardStates.New && card.DueAt <= now, cancellationToken);
        var newCount = await query.CountAsync(card => card.State == AnkiCardStates.New, cancellationToken);
        var learning = await query.CountAsync(card => card.State == AnkiCardStates.Learning || card.State == AnkiCardStates.Relearning, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var reviewQuery = _context.AnkiReviews.AsNoTracking().Where(review => review.AnkiCollectionId == collection.Id);
        var studied = IsSqlite()
            ? (await reviewQuery.Select(review => review.ReviewedAt).ToListAsync(cancellationToken)).Count(reviewedAt => reviewedAt >= dayStart)
            : await reviewQuery.CountAsync(review => review.ReviewedAt >= dayStart, cancellationToken);
        return new AnkiCollectionCounts(due, newCount, learning, total, studied);
    }

    internal static DateTimeOffset StartOfCollectionDay(string timeZoneId, DateTimeOffset now)
    {
        var zone = FindTimeZone(timeZoneId);
        var local = TimeZoneInfo.ConvertTime(now, zone);
        var localMidnight = DateTime.SpecifyKind(local.Date, DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight)).ToUniversalTime();
    }

    private bool IsSqlite() => _context.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true;

    internal static DateTimeOffset StartOfNextCollectionDay(string timeZoneId, DateTimeOffset now)
    {
        var zone = FindTimeZone(timeZoneId);
        var nextDate = TimeZoneInfo.ConvertTime(now, zone).Date.AddDays(1);
        var localMidnight = DateTime.SpecifyKind(nextDate, DateTimeKind.Unspecified);
        return new DateTimeOffset(localMidnight, zone.GetUtcOffset(localMidnight)).ToUniversalTime();
    }

    private async Task<AnkiCollection?> OwnedCollectionAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        await _context.AnkiCollections.SingleOrDefaultAsync(
            collection => collection.Id == id && collection.UserId == userId,
            cancellationToken);

    private async Task<bool> OwnsCollectionAsync(Guid id, string userId, CancellationToken cancellationToken) =>
        await _context.AnkiCollections.AnyAsync(
            collection => collection.Id == id && collection.UserId == userId,
            cancellationToken);

    private async Task<AnkiNote?> FindNoteAsync(
        Guid collectionId,
        string? wordId,
        Guid? sentenceId,
        CancellationToken cancellationToken) =>
        await _context.AnkiNotes.Include(note => note.Cards).SingleOrDefaultAsync(
            note => note.AnkiCollectionId == collectionId
                && (wordId != null ? note.WordId == wordId : note.SentenceId == sentenceId),
            cancellationToken);

    private AnkiNote GetOrCreateNote(
        List<AnkiNote> notes,
        Guid collectionId,
        Guid quizId,
        string itemType,
        string? wordId,
        Guid? sentenceId,
        string target,
        string source)
    {
        var note = notes.FirstOrDefault(item => wordId is not null
            ? item.WordId == wordId
            : item.SentenceId == sentenceId);
        if (note is not null)
        {
            note.TargetText = target.Trim();
            note.SourceText = source.Trim();
            return note;
        }
        note = NewNote(collectionId, quizId, itemType, wordId, sentenceId, target, source);
        notes.Add(note);
        _context.AnkiNotes.Add(note);
        return note;
    }

    private AnkiNote NewNote(
        Guid collectionId,
        Guid quizId,
        string itemType,
        string? wordId,
        Guid? sentenceId,
        string target,
        string source)
    {
        var now = _timeProvider.GetUtcNow();
        return new AnkiNote
        {
            Id = Guid.NewGuid(),
            AnkiCollectionId = collectionId,
            QuizId = quizId,
            ItemType = itemType,
            WordId = wordId,
            SentenceId = sentenceId,
            TargetText = target.Trim(),
            SourceText = source.Trim(),
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private static void EnsureDirectCard(AnkiNote note, string direction)
    {
        var card = EnsureCard(note, direction);
        card.DirectlyIncluded = true;
        card.IsActive = true;
    }

    private static void EnsureLinkedCard(AnkiNote note, string direction)
    {
        var card = EnsureCard(note, direction);
        card.QuizLinkIncluded = true;
        card.IsActive = !card.ExcludedFromQuizLink || card.DirectlyIncluded;
    }

    private static AnkiCard EnsureCard(AnkiNote note, string direction)
    {
        var card = note.Cards.FirstOrDefault(item => item.Direction == direction);
        if (card is not null)
            return card;
        card = new AnkiCard
        {
            Id = Guid.NewGuid(),
            AnkiNoteId = note.Id,
            Direction = direction,
            State = AnkiCardStates.New,
            IsActive = true,
        };
        note.Cards.Add(card);
        return card;
    }

    private static bool Matches(AnkiCollection collection, Quiz quiz) =>
        string.Equals(collection.SourceLanguage, quiz.SourceLanguage, StringComparison.OrdinalIgnoreCase)
        && string.Equals(collection.TargetLanguage, quiz.TargetLanguage, StringComparison.OrdinalIgnoreCase);

    private static string Required(string? value, string error, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new InvalidOperationException(error);
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }

    private static string NormalizeTimeZone(string? value)
    {
        var zone = FindTimeZone(value);
        return zone.Id;
    }

    private static TimeZoneInfo FindTimeZone(string? value)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(string.IsNullOrWhiteSpace(value) ? "UTC" : value);
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
