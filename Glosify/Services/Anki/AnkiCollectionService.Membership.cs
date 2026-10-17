using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed partial class AnkiCollectionService
{
    // Browser mutations validate before GetOrCreateAsync can persist a collection or
    // change its timezone. Creation and the subsequent writes share one transaction.
    public Task<bool> AddQuizForLanguageAsync(string targetLanguage, string? timeZoneId, Guid quizId,
        bool includeSentences, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            var canonical = CanonicalLanguageName(targetLanguage);
            var quiz = await _context.Quizzes.AsNoTracking().SingleOrDefaultAsync(
                quiz => quiz.Id == quizId && quiz.UserId == userId, cancellationToken);
            if (quiz is null || !SameLanguage(canonical, QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language)))
                return false;
            var collection = await GetOrCreateAsync(userId, canonical, timeZoneId, cancellationToken);
            var link = await _context.AnkiQuizLinks.SingleOrDefaultAsync(
                item => item.AnkiCollectionId == collection.Id && item.QuizId == quizId, cancellationToken);
            if (link is null)
                return await ApplyQuizAsync(new(collection.Id, quizId, true, true, includeSentences, includeSentences),
                    collection, cancellationToken);

            // The picker still offers linked quizzes. Adding one again only adds content: it
            // keeps its sentences and directions, and items the user unticked stay unticked.
            var addWordsForward = !link.WordsSourceToTarget;
            var addWordsReverse = !link.WordsTargetToSource;
            var addSentencesForward = includeSentences && !link.SentencesSourceToTarget;
            var addSentencesReverse = includeSentences && !link.SentencesTargetToSource;
            if (!addWordsForward && !addWordsReverse && !addSentencesForward && !addSentencesReverse) return true;
            var unticked = await _context.AnkiNotes
                .Where(note => note.AnkiCollectionId == collection.Id && note.QuizId == quizId
                    && note.Cards.Any(card => card.ExcludedFromQuizLink))
                .Select(note => note.Id)
                .ToListAsync(cancellationToken);
            link.WordsSourceToTarget = link.WordsTargetToSource = true;
            if (includeSentences) link.SentencesSourceToTarget = link.SentencesTargetToSource = true;
            link.UpdatedAt = collection.UpdatedAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
            await SyncCollectionAsync(collection.Id, cancellationToken);
            // A newly linked direction of an unticked item is unticked too.
            var newCards = await _context.AnkiCards
                .Where(card => unticked.Contains(card.AnkiNoteId) && card.QuizLinkIncluded
                    && !card.ExcludedFromQuizLink && !card.DirectlyIncluded
                    && (card.Note.ItemType == PracticeItemType.Words
                        && (addWordsForward && card.Direction == PracticeDirection.SourceToTarget
                            || addWordsReverse && card.Direction == PracticeDirection.TargetToSource)
                        || card.Note.ItemType == PracticeItemType.Sentences
                        && (addSentencesForward && card.Direction == PracticeDirection.SourceToTarget
                            || addSentencesReverse && card.Direction == PracticeDirection.TargetToSource)))
                .ToListAsync(cancellationToken);
            foreach (var card in newCards)
            {
                card.ExcludedFromQuizLink = true;
                card.IsActive = false;
            }
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<bool> AddItemForLanguageAsync(string targetLanguage, string? timeZoneId, AnkiItemKey item,
        string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            var canonical = CanonicalLanguageName(targetLanguage);
            IReadOnlyList<MembershipItem> items;
            try
            {
                items = await ReadMembershipAdditionsAsync([item], canonical, userId, cancellationToken);
            }
            catch (AnkiValidationException)
            {
                // Preserve the single-item endpoint's not-found response for stale ids.
                return false;
            }
            var collection = await GetOrCreateAsync(userId, canonical, timeZoneId, cancellationToken);
            await ApplyMembershipAsync(collection, items, [], cancellationToken);
            return true;
        }, cancellationToken);

    public Task<bool> SetMembershipForLanguageAsync(string targetLanguage, string? timeZoneId,
        IReadOnlyList<AnkiItemKey> add, IReadOnlyList<AnkiItemKey> remove, string userId,
        CancellationToken cancellationToken = default)
    {
        ValidateMembershipSize(add, remove);
        return ExecuteAtomicallyAsync(async () =>
        {
            var canonical = CanonicalLanguageName(targetLanguage);
            var items = await ReadMembershipAdditionsAsync(add, canonical, userId, cancellationToken);
            // Removing stale selections must not create an empty collection either.
            if (items.Count == 0 && await FindForLanguageAsync(userId, canonical, cancellationToken) is null)
                return true;
            var collection = await GetOrCreateAsync(userId, canonical, timeZoneId, cancellationToken);
            await ApplyMembershipAsync(collection, items, remove, cancellationToken);
            return true;
        }, cancellationToken);
    }

    public Task<bool> SetMembershipAsync(Guid collectionId, IReadOnlyList<AnkiItemKey> add,
        IReadOnlyList<AnkiItemKey> remove, string userId, CancellationToken cancellationToken = default)
    {
        ValidateMembershipSize(add, remove);
        return ExecuteAtomicallyAsync(async () =>
        {
            var collection = await OwnedCollectionAsync(collectionId, userId, cancellationToken);
            if (collection is null) return false;
            var items = await ReadMembershipAdditionsAsync(add, collection.TargetLanguage, userId, cancellationToken);
            await ApplyMembershipAsync(collection, items, remove, cancellationToken);
            return true;
        }, cancellationToken);
    }

    private static void ValidateMembershipSize(IReadOnlyList<AnkiItemKey> add, IReadOnlyList<AnkiItemKey> remove)
    {
        if ((long)add.Count + remove.Count > MaximumMembershipChanges)
            throw new AnkiValidationException("Save at most 2,000 changes at a time.");
    }

    private sealed record MembershipItem(AnkiItemKey Key, string Target, string Source, string SourceLanguage);

    private static AnkiItemKey NormalizeMembershipKey(AnkiItemKey item)
    {
        var type = string.Equals(item.ItemType, "sentence", StringComparison.OrdinalIgnoreCase)
            ? PracticeItemType.Sentences : PracticeItemType.Normalize(item.ItemType);
        var id = type == PracticeItemType.Sentences && Guid.TryParse(item.ItemId, out var sentenceId)
            ? sentenceId.ToString() : item.ItemId;
        return item with { ItemType = type, ItemId = id };
    }

    private async Task<IReadOnlyList<MembershipItem>> ReadMembershipAdditionsAsync(
        IReadOnlyList<AnkiItemKey> add, string targetLanguage, string userId, CancellationToken cancellationToken)
    {
        if (add.Count == 0) return [];
        var keys = add.Select(NormalizeMembershipKey).Distinct().ToList();
        if (keys.Any(key => string.IsNullOrWhiteSpace(key.ItemId)
            || key.ItemType == PracticeItemType.Sentences && !Guid.TryParse(key.ItemId, out _)))
            throw new AnkiValidationException("Invalid item id.");
        var quizIds = keys.Select(key => key.QuizId).Distinct().ToArray();
        var quizzes = await _context.Quizzes.AsNoTracking()
            .Where(quiz => quizIds.Contains(quiz.Id) && quiz.UserId == userId)
            .ToDictionaryAsync(quiz => quiz.Id, cancellationToken);
        if (quizzes.Count != quizIds.Length || quizzes.Values.Any(quiz =>
            !SameLanguage(targetLanguage, QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language))))
            throw new AnkiValidationException("The quiz and Anki collection must belong to you and use the same learning language.");

        var wordIds = keys.Where(key => key.ItemType == PracticeItemType.Words).Select(key => key.ItemId).ToArray();
        var sentenceIds = keys.Where(key => key.ItemType == PracticeItemType.Sentences).Select(key => Guid.Parse(key.ItemId)).ToArray();
        var words = await _context.Words.AsNoTracking().Where(word => wordIds.Contains(word.Id))
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        var sentences = await _context.QuizSentences.AsNoTracking().Where(sentence => sentenceIds.Contains(sentence.Id))
            .ToDictionaryAsync(sentence => sentence.Id, cancellationToken);
        var result = new List<MembershipItem>();
        foreach (var key in keys)
        {
            var sourceLanguage = quizzes[key.QuizId].SourceLanguage;
            if (key.ItemType == PracticeItemType.Words
                && words.TryGetValue(key.ItemId, out var word) && word.QuizId == key.QuizId)
                result.Add(new(key, word.Lemma, word.Translation, sourceLanguage));
            else if (key.ItemType == PracticeItemType.Sentences
                && sentences.TryGetValue(Guid.Parse(key.ItemId), out var sentence) && sentence.QuizId == key.QuizId)
                result.Add(new(key, sentence.Text, sentence.Translation, sourceLanguage));
            else
                throw new AnkiValidationException("One or more selected items no longer belong to this quiz. Read the quiz again before retrying.");
        }
        return result;
    }

    private async Task ApplyMembershipAsync(AnkiCollection collection, IReadOnlyList<MembershipItem> add,
        IReadOnlyList<AnkiItemKey> remove, CancellationToken cancellationToken)
    {
        // Validate the whole request before synchronizing, then synchronize only once.
        // Removal-only saves need only the selected notes and preserve scheduling history.
        if (add.Count > 0) await SyncCollectionAsync(collection.Id, cancellationToken);
        var removals = remove.Select(NormalizeMembershipKey).Distinct().ToList();
        var keys = add.Select(item => item.Key).Concat(removals).ToList();
        var wordIds = keys.Where(key => key.ItemType == PracticeItemType.Words).Select(key => key.ItemId).Distinct().ToArray();
        var sentenceIds = keys.Where(key => key.ItemType == PracticeItemType.Sentences && Guid.TryParse(key.ItemId, out _))
            .Select(key => Guid.Parse(key.ItemId)).Distinct().ToArray();
        var notes = await _context.AnkiNotes.Include(note => note.Cards)
            .Where(note => note.AnkiCollectionId == collection.Id
                && (wordIds.Contains(note.WordId!) || note.SentenceId.HasValue && sentenceIds.Contains(note.SentenceId.Value)))
            .ToListAsync(cancellationToken);
        var byWord = notes.Where(note => note.WordId is not null).ToDictionary(note => note.WordId!, StringComparer.Ordinal);
        var bySentence = notes.Where(note => note.SentenceId.HasValue).ToDictionary(note => note.SentenceId!.Value);
        foreach (var item in add)
        {
            var key = item.Key;
            var wordId = key.ItemType == PracticeItemType.Words ? key.ItemId : null;
            Guid? sentenceId = wordId is null ? Guid.Parse(key.ItemId) : null;
            var note = GetOrCreateNote(notes, byWord, bySentence, collection.Id, key.QuizId, key.ItemType,
                wordId, sentenceId, item.Target, item.Source, item.SourceLanguage);
            EnsureDirectCard(note, PracticeDirection.SourceToTarget);
            EnsureDirectCard(note, PracticeDirection.TargetToSource);
            note.IsActive = true;
        }
        foreach (var key in removals)
        {
            var note = key.ItemType == PracticeItemType.Words ? byWord.GetValueOrDefault(key.ItemId)
                : Guid.TryParse(key.ItemId, out var id) ? bySentence.GetValueOrDefault(id) : null;
            if (note is null || note.QuizId != key.QuizId) continue;
            foreach (var card in note.Cards)
            {
                card.DirectlyIncluded = false;
                card.ExcludedFromQuizLink = card.QuizLinkIncluded;
                card.IsActive = false;
            }
            note.IsActive = false;
            note.UpdatedAt = _timeProvider.GetUtcNow();
        }
        collection.UpdatedAt = _timeProvider.GetUtcNow();
        await _context.SaveChangesAsync(cancellationToken);
    }
}
