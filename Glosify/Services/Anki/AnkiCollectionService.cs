using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed partial class AnkiCollectionService : IAnkiCollectionService
{
    public const int MaximumMembershipChanges = 2_000;

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
        var collections = await _context.AnkiCollections
            .AsNoTracking()
            .Where(collection => collection.UserId == userId)
            .OrderBy(collection => collection.TargetLanguage)
            .ToListAsync(cancellationToken);
        foreach (var collection in collections)
            await SyncCollectionAsync(collection.Id, cancellationToken);

        var now = _timeProvider.GetUtcNow();
        var summaries = new List<AnkiCollectionSummary>(collections.Count);
        foreach (var collection in collections)
        {
            summaries.Add(new AnkiCollectionSummary(
                collection.Id,
                collection.Name,
                collection.TargetLanguage,
                collection.DefaultDirection,
                await CountsAsync(collection, now, null, cancellationToken)));
        }
        return summaries;
    }

    public async Task<AnkiCollection?> FindForLanguageAsync(
        string userId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalLanguageName(targetLanguage);
        return await _context.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(
            collection => collection.UserId == userId && collection.TargetLanguage == canonical,
            cancellationToken);
    }

    public async Task<AnkiCollection> GetOrCreateAsync(
        string userId,
        string targetLanguage,
        string? timeZoneId,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalLanguageName(targetLanguage);
        var existing = await TrackedForLanguageAsync(userId, canonical, cancellationToken)
            ?? await CreateAsync(userId, canonical, timeZoneId, cancellationToken);
        // The browser reports its zone with every add, so the study day follows
        // the user after travel without a separate setting.
        if (!string.IsNullOrWhiteSpace(timeZoneId) && NormalizeTimeZone(timeZoneId) is var zone && zone != existing.TimeZoneId)
        {
            existing.TimeZoneId = zone;
            existing.UpdatedAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
        }
        return existing;
    }

    private Task<AnkiCollection?> TrackedForLanguageAsync(string userId, string canonical, CancellationToken cancellationToken) =>
        _context.AnkiCollections.SingleOrDefaultAsync(
            collection => collection.UserId == userId && collection.TargetLanguage == canonical,
            cancellationToken);

    private async Task<AnkiCollection> CreateAsync(string userId, string canonical, string? timeZoneId,
        CancellationToken cancellationToken)
    {
        // Accounting mutates tracked snapshots and counters before the database save.
        // Keep the caller's unit of work so duplicate recovery can undo those mutations
        // without discarding unrelated edits or detaching entities the caller still uses.
        var initial = _context.ChangeTracker.Entries().Select(entry => (Entry: entry, entry.State,
            Current: entry.CurrentValues.Clone(), Original: entry.OriginalValues.Clone(),
            Modified: entry.Properties.Where(property => property.IsModified).Select(property => property.Metadata.Name).ToHashSet())).ToArray();
        var entities = initial.Select(item => item.Entry.Entity).ToHashSet(ReferenceEqualityComparer.Instance);
        var now = _timeProvider.GetUtcNow();
        var collection = new AnkiCollection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = canonical,
            TargetLanguage = canonical,
            TimeZoneId = NormalizeTimeZone(timeZoneId),
            CreatedAt = now,
            UpdatedAt = now,
        };
        _context.AnkiCollections.Add(collection);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
            return collection;
        }
        catch (DbUpdateException exception)
        {
            // Inside a caller's transaction EF rolls the failed save back to its savepoint,
            // but that does not restore the tracker, including accounting changes.
            foreach (var entry in _context.ChangeTracker.Entries().Where(entry => !entities.Contains(entry.Entity)).ToArray())
                entry.State = EntityState.Detached;
            foreach (var item in initial)
            {
                item.Entry.CurrentValues.SetValues(item.Current);
                item.Entry.State = item.State;
                item.Entry.OriginalValues.SetValues(item.Original);
                if (item.State == EntityState.Modified)
                    foreach (var property in item.Entry.Properties)
                        property.IsModified = item.Modified.Contains(property.Metadata.Name);
            }
            // SaveChanges also writes the caller's other pending edits. Only recover
            // a failure attributed to this insert, never hide an unrelated write error.
            // SQL Server can attribute a failed batch to several entries, so also
            // identify the specific unique index reported by the provider.
            var expectedDuplicate = _context.Database.IsSqlServer()
                ? exception.InnerException is Microsoft.Data.SqlClient.SqlException { Number: 2601 or 2627 } sql
                    && sql.Message.Contains("IX_AnkiCollections_UserId_TargetLanguage", StringComparison.Ordinal)
                : exception.Entries.Count == 1;
            if (!expectedDuplicate || !exception.Entries.Any(entry => ReferenceEquals(entry.Entity, collection)))
                throw;
            var winner = await TrackedForLanguageAsync(userId, canonical, cancellationToken);
            if (winner is null) throw;
            return winner;
        }
    }

    public async Task<bool> IsOwnedByLanguageAsync(
        Guid collectionId,
        string targetLanguage,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var canonicalTarget = CanonicalLanguageName(targetLanguage);
        var storedTarget = await _context.AnkiCollections.AsNoTracking()
            .Where(collection => collection.Id == collectionId
                && collection.UserId == userId)
            .Select(collection => collection.TargetLanguage)
            .SingleOrDefaultAsync(cancellationToken);
        return storedTarget is not null && SameLanguage(storedTarget, canonicalTarget);
    }

    public async Task<AnkiWorkspace> GetWorkspaceAsync(
        string userId,
        string targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var canonical = CanonicalLanguageName(targetLanguage);
        var collection = await FindForLanguageAsync(userId, canonical, cancellationToken);
        IReadOnlyList<AnkiQuizLink> links = [];
        var included = new HashSet<string>(StringComparer.Ordinal);
        if (collection is not null)
        {
            await SyncCollectionAsync(collection.Id, cancellationToken);
            links = await _context.AnkiQuizLinks
                .AsNoTracking()
                .Include(link => link.Quiz)
                .Where(link => link.AnkiCollectionId == collection.Id)
                .OrderBy(link => link.Quiz.Name)
                .ToListAsync(cancellationToken);
            var activeNotes = await _context.AnkiNotes.AsNoTracking()
                .Where(note => note.AnkiCollectionId == collection.Id && note.Cards.Any(card => card.IsActive))
                .Select(note => new { note.WordId, note.SentenceId })
                .ToListAsync(cancellationToken);
            foreach (var note in activeNotes)
                included.Add(note.WordId is not null ? ItemKey(PracticeItemType.Words, note.WordId) : ItemKey(PracticeItemType.Sentences, note.SentenceId!.Value.ToString()));
        }

        var quizzes = (await _context.Quizzes
            .AsNoTracking()
            .Where(quiz => quiz.UserId == userId)
            .OrderBy(quiz => quiz.Name)
            .ToListAsync(cancellationToken))
            .Where(quiz => SameLanguage(QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language), canonical))
            .ToList();
        var quizIds = quizzes.Select(quiz => quiz.Id).ToList();
        var quizzesById = quizzes.ToDictionary(quiz => quiz.Id);
        var words = (await _context.Words.AsNoTracking()
                .Where(word => quizIds.Contains(word.QuizId))
                .OrderBy(word => word.CreatedAt)
                .Select(word => new { word.QuizId, word.Id, word.Lemma, word.Translation })
                .ToListAsync(cancellationToken))
            .Select(word => Item(quizzesById[word.QuizId], PracticeItemType.Words, word.Id, word.Lemma, word.Translation));
        var sentences = (await _context.QuizSentences.AsNoTracking()
                .Where(sentence => quizIds.Contains(sentence.QuizId))
                .OrderBy(sentence => sentence.CreatedAt)
                .Select(sentence => new { sentence.QuizId, sentence.Id, sentence.Text, sentence.Translation })
                .ToListAsync(cancellationToken))
            .Select(sentence => Item(quizzesById[sentence.QuizId], PracticeItemType.Sentences, sentence.Id.ToString(), sentence.Text, sentence.Translation));

        return new AnkiWorkspace(collection, links, quizzes, words.Concat(sentences).ToList());

        AnkiAvailableItem Item(Quiz quiz, string itemType, string id, string target, string source) =>
            new(quiz.Id, quiz.Name, quiz.SourceLanguage, itemType, id, target, source, included.Contains(ItemKey(itemType, id)));
    }

    private static string ItemKey(string itemType, string id) => itemType + ":" + id;

    public async Task<bool> SetPracticeDirectionAsync(Guid collectionId, string direction, string userId,
        CancellationToken cancellationToken = default)
    {
        if (direction is not (PracticeDirection.SourceToTarget or PracticeDirection.TargetToSource))
            throw new AnkiValidationException("Choose source-to-target or target-to-source.");
        var collection = await OwnedCollectionAsync(collectionId, userId, cancellationToken);
        if (collection is null)
            return false;
        if (collection.DefaultDirection != direction)
        {
            collection.DefaultDirection = direction;
            collection.UpdatedAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
        }
        return true;
    }

    public async Task<bool> UpdateTimeZoneAsync(
        Guid collectionId,
        string timeZoneId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var collection = await OwnedCollectionAsync(collectionId, userId, cancellationToken);
        if (collection is null)
            return false;
        collection.TimeZoneId = NormalizeTimeZone(timeZoneId);
        collection.UpdatedAt = _timeProvider.GetUtcNow();
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
            throw new AnkiValidationException("Choose at least one content type and direction.");
        }
        return await ExecuteAtomicallyAsync(
            () => AddQuizCoreAsync(input, userId, cancellationToken), cancellationToken);
    }

    private async Task<bool> AddQuizCoreAsync(
        AddAnkiQuizInput input,
        string userId,
        CancellationToken cancellationToken)
    {
        var collection = await OwnedCollectionAsync(input.CollectionId, userId, cancellationToken);
        var quiz = await _context.Quizzes.SingleOrDefaultAsync(
            item => item.Id == input.QuizId && item.UserId == userId,
            cancellationToken);
        if (collection is null || quiz is null || !Matches(collection, quiz))
            return false;

        return await ApplyQuizAsync(input, collection, cancellationToken);
    }

    private async Task<bool> ApplyQuizAsync(AddAnkiQuizInput input, AnkiCollection collection,
        CancellationToken cancellationToken)
    {
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
        return await ExecuteAtomicallyAsync(async () =>
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
        }, cancellationToken);
    }

    public async Task<bool> AddItemAsync(
        AddAnkiItemInput input,
        string userId,
        CancellationToken cancellationToken = default)
    {
        if (!input.SourceToTarget && !input.TargetToSource)
            throw new AnkiValidationException("Choose at least one direction.");
        return await ExecuteAtomicallyAsync(
            () => AddItemCoreAsync(input, userId, cancellationToken), cancellationToken);
    }

    private async Task<bool> AddItemCoreAsync(
        AddAnkiItemInput input,
        string userId,
        CancellationToken cancellationToken)
    {
        var collection = await OwnedCollectionAsync(input.CollectionId, userId, cancellationToken);
        var quiz = await _context.Quizzes.AsNoTracking().SingleOrDefaultAsync(
            item => item.Id == input.QuizId && item.UserId == userId,
            cancellationToken);
        if (collection is null || quiz is null || !Matches(collection, quiz))
            return false;

        await SyncCollectionAsync(input.CollectionId, cancellationToken);
        // Individual-item forms use singular labels; stored notes use the
        // canonical practice type, also accepted from existing callers.
        var itemType = string.Equals(input.ItemType, "sentence", StringComparison.OrdinalIgnoreCase)
            ? PracticeItemType.Sentences
            : PracticeItemType.Normalize(input.ItemType);
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
            note = NewNote(input.CollectionId, input.QuizId, itemType, wordId, sentenceId, target, source, quiz.SourceLanguage);
            _context.AnkiNotes.Add(note);
        }
        else
        {
            note.TargetText = target;
            note.SourceText = source;
            note.SourceLanguage = quiz.SourceLanguage;
            note.IsActive = true;
            note.UpdatedAt = _timeProvider.GetUtcNow();
        }
        if (input.SourceToTarget)
            EnsureDirectCard(note, PracticeDirection.SourceToTarget);
        if (input.TargetToSource)
            EnsureDirectCard(note, PracticeDirection.TargetToSource);
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
        var wordsByQuiz = words.ToLookup(word => word.QuizId);
        var sentencesByQuiz = sentences.ToLookup(sentence => sentence.QuizId);
        var notesByWord = notes.Where(note => note.WordId is not null)
            .ToDictionary(note => note.WordId!, StringComparer.Ordinal);
        var notesBySentence = notes.Where(note => note.SentenceId.HasValue)
            .ToDictionary(note => note.SentenceId!.Value);

        foreach (var note in notes)
        {
            foreach (var card in note.Cards)
                card.QuizLinkIncluded = false;
            var sourceLanguage = quizzes.TryGetValue(note.QuizId, out var noteQuiz) ? noteQuiz.SourceLanguage : note.SourceLanguage;
            if (note.WordId is not null && wordsById.TryGetValue(note.WordId, out var word))
            {
                UpdateSnapshot(note, word.Lemma, word.Translation, sourceLanguage);
            }
            else if (note.SentenceId.HasValue && sentencesById.TryGetValue(note.SentenceId.Value, out var sentence))
            {
                UpdateSnapshot(note, sentence.Text, sentence.Translation, sourceLanguage);
            }
        }

        foreach (var link in collection.QuizLinks)
        {
            if (!quizzes.TryGetValue(link.QuizId, out var quiz) || !Matches(collection, quiz))
                continue;
            foreach (var word in wordsByQuiz[link.QuizId])
            {
                var note = GetOrCreateNote(notes, notesByWord, notesBySentence, collectionId, link.QuizId, PracticeItemType.Words, word.Id, null, word.Lemma, word.Translation, quiz.SourceLanguage);
                if (link.WordsSourceToTarget)
                    EnsureLinkedCard(note, PracticeDirection.SourceToTarget);
                if (link.WordsTargetToSource)
                    EnsureLinkedCard(note, PracticeDirection.TargetToSource);
            }
            foreach (var sentence in sentencesByQuiz[link.QuizId])
            {
                var note = GetOrCreateNote(notes, notesByWord, notesBySentence, collectionId, link.QuizId, PracticeItemType.Sentences, null, sentence.Id, sentence.Text, sentence.Translation, quiz.SourceLanguage);
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
            var isActive = note.Cards.Any(card => card.IsActive);
            if (note.IsActive != isActive)
            {
                note.IsActive = isActive;
                note.UpdatedAt = _timeProvider.GetUtcNow();
            }
        }
        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<AnkiCollectionCounts> CountsAsync(
        AnkiCollection collection,
        DateTimeOffset now,
        string? direction,
        CancellationToken cancellationToken)
    {
        var dayStart = StartOfCollectionDay(collection.TimeZoneId, now);
        var query = _context.AnkiCards.AsNoTracking()
            .Where(card => card.Note.AnkiCollectionId == collection.Id && card.IsActive
                && (direction == null || card.Direction == direction));
        var due = IsSqlite()
            ? (await query.ToListAsync(cancellationToken)).Count(card => card.State != AnkiCardStates.New && card.DueAt <= now)
            : await query.CountAsync(card => card.State != AnkiCardStates.New && card.DueAt <= now, cancellationToken);
        var newCount = await query.CountAsync(card => card.State == AnkiCardStates.New, cancellationToken);
        var learning = await query.CountAsync(card => card.State == AnkiCardStates.Learning || card.State == AnkiCardStates.Relearning, cancellationToken);
        var total = await query.CountAsync(cancellationToken);
        var reviewQuery = _context.AnkiReviews.AsNoTracking().Where(review => review.AnkiCollectionId == collection.Id
            && (direction == null || review.Card.Direction == direction));
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

    private async Task<T> ExecuteAtomicallyAsync<T>(
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational() || _context.Database.CurrentTransaction is not null)
            return await operation();

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async token =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(token);
            try
            {
                var result = await operation();
                await transaction.CommitAsync(token);
                return result;
            }
            catch
            {
                _context.ChangeTracker.Clear();
                throw;
            }
        }, cancellationToken);
    }

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
        Dictionary<string, AnkiNote> notesByWord,
        Dictionary<Guid, AnkiNote> notesBySentence,
        Guid collectionId,
        Guid quizId,
        string itemType,
        string? wordId,
        Guid? sentenceId,
        string target,
        string source,
        string sourceLanguage)
    {
        AnkiNote? note = wordId is not null
            ? notesByWord.GetValueOrDefault(wordId)
            : sentenceId.HasValue ? notesBySentence.GetValueOrDefault(sentenceId.Value) : null;
        if (note is not null)
        {
            UpdateSnapshot(note, target, source, sourceLanguage);
            return note;
        }
        note = NewNote(collectionId, quizId, itemType, wordId, sentenceId, target, source, sourceLanguage);
        notes.Add(note);
        if (wordId is not null)
            notesByWord[wordId] = note;
        else if (sentenceId.HasValue)
            notesBySentence[sentenceId.Value] = note;
        _context.AnkiNotes.Add(note);
        return note;
    }

    private void UpdateSnapshot(AnkiNote note, string target, string source, string sourceLanguage)
    {
        var normalizedTarget = target.Trim();
        var normalizedSource = source.Trim();
        if (note.TargetText == normalizedTarget && note.SourceText == normalizedSource && note.SourceLanguage == sourceLanguage)
            return;
        note.TargetText = normalizedTarget;
        note.SourceText = normalizedSource;
        note.SourceLanguage = sourceLanguage;
        note.UpdatedAt = _timeProvider.GetUtcNow();
    }

    private AnkiNote NewNote(
        Guid collectionId,
        Guid quizId,
        string itemType,
        string? wordId,
        Guid? sentenceId,
        string target,
        string source,
        string sourceLanguage)
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
            SourceLanguage = sourceLanguage,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    private void EnsureDirectCard(AnkiNote note, string direction)
    {
        var card = EnsureCard(note, direction);
        card.DirectlyIncluded = true;
        card.IsActive = true;
    }

    private void EnsureLinkedCard(AnkiNote note, string direction)
    {
        var card = EnsureCard(note, direction);
        card.QuizLinkIncluded = true;
        card.IsActive = !card.ExcludedFromQuizLink || card.DirectlyIncluded;
    }

    private AnkiCard EnsureCard(AnkiNote note, string direction)
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
        // A client-assigned GUID on a new child of an existing note must be explicitly
        // Added; relationship discovery can otherwise treat it as an existing row.
        _context.AnkiCards.Add(card);
        return card;
    }

    // Quizzes from any source language share the collection for their learning language.
    private static bool Matches(AnkiCollection collection, Quiz quiz) =>
        SameLanguage(collection.TargetLanguage, QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language));

    private static bool SameLanguage(string left, string right)
    {
        var normalizedLeft = QuizLanguageCatalog.Find(left)?.Name ?? left.Trim();
        var normalizedRight = QuizLanguageCatalog.Find(right)?.Name ?? right.Trim();
        return string.Equals(normalizedLeft, normalizedRight, StringComparison.OrdinalIgnoreCase);
    }

    private static string Required(string? value, string error, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0)
            throw new AnkiValidationException(error);
        if (normalized.Length > maxLength)
            throw new AnkiValidationException($"Value cannot exceed {maxLength} characters.");
        return normalized;
    }

    private static string CanonicalLanguageName(string? value)
    {
        var candidate = Required(value, "Choose an app language.", 64);
        return QuizLanguageCatalog.Find(candidate)?.Name
            ?? throw new AnkiValidationException("Choose a supported app language.");
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
