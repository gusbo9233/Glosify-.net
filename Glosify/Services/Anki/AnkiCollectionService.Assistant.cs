using Glosify.Models;
using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed partial class AnkiCollectionService
{
    public const int AssistantPageSize = 50;

    // Assistant reads are deliberately observational; MVC reads may synchronize collections.
    public async Task<AnkiPage<AnkiCollectionSummary>> BrowseAsync(string userId, int offset, CancellationToken cancellationToken = default)
    {
        offset = Math.Max(0, offset);
        var query = _context.AnkiCollections.AsNoTracking().Where(c => c.UserId == userId);
        var total = await query.CountAsync(cancellationToken);
        var rows = await query.OrderBy(c => c.Name).ThenBy(c => c.Id).Skip(offset).Take(AssistantPageSize).ToListAsync(cancellationToken);
        var summaries = new List<AnkiCollectionSummary>();
        foreach (var collection in rows)
            summaries.Add(await SummaryAsync(collection, cancellationToken));
        return new(summaries, total, Next(offset, rows.Count, total));
    }

    public async Task<AnkiInspection?> InspectAsync(Guid collectionId, string userId, int cardOffset, int linkOffset, CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections.AsNoTracking().SingleOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);
        if (collection is null) return null;
        cardOffset = Math.Max(0, cardOffset);
        linkOffset = Math.Max(0, linkOffset);
        var cards = _context.AnkiCards.AsNoTracking().Where(c => c.Note.AnkiCollectionId == collectionId && c.IsActive);
        var cardCount = await cards.CountAsync(cancellationToken);
        var rows = await cards.OrderBy(c => c.Note.TargetText).ThenBy(c => c.Id).Skip(cardOffset).Take(AssistantPageSize)
            .Select(c => new AnkiCardListItem(c.Id, c.AnkiNoteId, c.Note.QuizId, c.Note.ItemType, c.Note.TargetText,
                c.Note.SourceText, c.Direction, c.State, c.DueAt, c.DirectlyIncluded, c.QuizLinkIncluded)).ToListAsync(cancellationToken);
        var links = _context.AnkiQuizLinks.AsNoTracking().Where(l => l.AnkiCollectionId == collectionId);
        var linkCount = await links.CountAsync(cancellationToken);
        var linkRows = await links.OrderBy(l => l.Id).Skip(linkOffset).Take(AssistantPageSize).ToListAsync(cancellationToken);
        return new(await SummaryAsync(collection, cancellationToken), new(rows, cardCount, Next(cardOffset, rows.Count, cardCount)),
            new(linkRows.Select(AnkiLinkState.From).ToList(), linkCount, Next(linkOffset, linkRows.Count, linkCount)));
    }

    public async Task<AnkiCollectionCounts?> ReadCountsAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default)
    {
        var collection = await _context.AnkiCollections.AsNoTracking()
            .SingleOrDefaultAsync(c => c.Id == collectionId && c.UserId == userId, cancellationToken);
        return collection is null ? null : await CountsAsync(collection, _timeProvider.GetUtcNow(), cancellationToken);
    }

    private async Task<AnkiCollectionSummary> SummaryAsync(AnkiCollection c, CancellationToken ct) =>
        new(c.Id, c.Name, c.SourceLanguage, c.TargetLanguage, c.DefaultDirection, await CountsAsync(c, _timeProvider.GetUtcNow(), ct));

    private static int? Next(int offset, int count, int total) => offset + count < total ? offset + count : null;

    public Task<AnkiAdditionResult> AddItemsAsync(AddAnkiItemsInput input, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            await CompatibleOwnedAsync(input.CollectionId, input.QuizId, userId, cancellationToken);
            if (!input.SourceToTarget && !input.TargetToSource)
                throw new AnkiValidationException("Choose at least one direction.");
            if (input.ItemIds.Count is < 1 or > 100 || input.ItemIds.Any(string.IsNullOrWhiteSpace))
                throw new AnkiValidationException("Choose between 1 and 100 item ids per call.");
            if (input.ItemType is not (PracticeItemType.Words or PracticeItemType.Sentences))
                throw new AnkiValidationException("Choose words or sentences.");
            var ids = input.ItemIds.Distinct(StringComparer.Ordinal).ToArray();
            var items = new List<(string? WordId, Guid? SentenceId, string Target, string Source)>();
            if (input.ItemType == PracticeItemType.Words)
            {
                var words = await _context.Words.AsNoTracking().Where(w => w.QuizId == input.QuizId && ids.Contains(w.Id)).ToListAsync(cancellationToken);
                items.AddRange(words.Select(w => ((string?)w.Id, (Guid?)null, w.Lemma, w.Translation)));
            }
            else
            {
                if (ids.Any(id => !Guid.TryParse(id, out _))) throw new AnkiValidationException("Invalid sentence id.");
                var sentenceIds = ids.Select(Guid.Parse).Distinct().ToArray();
                var sentences = await _context.QuizSentences.AsNoTracking().Where(s => s.QuizId == input.QuizId && sentenceIds.Contains(s.Id)).ToListAsync(cancellationToken);
                items.AddRange(sentences.Select(s => ((string?)null, (Guid?)s.Id, s.Text, s.Translation)));
                ids = sentenceIds.Select(id => id.ToString()).ToArray();
            }
            if (items.Count != ids.Length)
                throw new AnkiValidationException("One or more selected items no longer belong to this quiz. Read the quiz again before retrying.");

            // Validate the entire selection before any synchronization or write.
            await SyncCollectionAsync(input.CollectionId, cancellationToken);
            var before = new Dictionary<Guid, AnkiCardState?>();
            var changed = new List<AnkiCard>();
            var already = 0;
            var added = 0;
            foreach (var item in items)
            {
                var note = await FindNoteAsync(input.CollectionId, item.WordId, item.SentenceId, cancellationToken);
                if (note is null)
                {
                    note = NewNote(input.CollectionId, input.QuizId, input.ItemType, item.WordId, item.SentenceId, item.Target, item.Source);
                    _context.AnkiNotes.Add(note);
                }
                foreach (var direction in Directions(input.SourceToTarget, input.TargetToSource))
                {
                    var card = note.Cards.SingleOrDefault(c => c.Direction == direction);
                    if (card is { IsActive: true })
                    {
                        already++;
                        if (card.DirectlyIncluded) continue;
                    }
                    else added++;
                    var old = card is null ? null : AnkiCardState.From(card);
                    card = EnsureCard(note, direction);
                    card.Note = note;
                    before[card.Id] = old;
                    card.DirectlyIncluded = true;
                    card.IsActive = true;
                    note.IsActive = true;
                    changed.Add(card);
                }
            }
            await _context.SaveChangesAsync(cancellationToken);
            return new AnkiAdditionResult(input.CollectionId, items.Count, added, already,
                changed.Select(c => new AnkiCardChange(before[c.Id], AnkiCardState.From(c))).ToList());
        }, cancellationToken);

    public Task<AnkiAdditionResult> LinkQuizAdditiveAsync(AddAnkiQuizInput input, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            await CompatibleOwnedAsync(input.CollectionId, input.QuizId, userId, cancellationToken);
            if (!input.WordsSourceToTarget && !input.WordsTargetToSource && !input.SentencesSourceToTarget && !input.SentencesTargetToSource)
                throw new AnkiValidationException("Choose at least one content type and direction.");
            var before = await LinkSnapshotAsync(input.CollectionId, input.QuizId, cancellationToken);
            var link = await _context.AnkiQuizLinks.SingleOrDefaultAsync(l => l.AnkiCollectionId == input.CollectionId && l.QuizId == input.QuizId, cancellationToken);
            var changed = link is null || input.WordsSourceToTarget && !link.WordsSourceToTarget || input.WordsTargetToSource && !link.WordsTargetToSource
                || input.SentencesSourceToTarget && !link.SentencesSourceToTarget || input.SentencesTargetToSource && !link.SentencesTargetToSource;
            if (link is null)
            {
                link = new AnkiQuizLink { Id = Guid.NewGuid(), AnkiCollectionId = input.CollectionId, QuizId = input.QuizId, CreatedAt = _timeProvider.GetUtcNow() };
                _context.AnkiQuizLinks.Add(link);
            }
            link.WordsSourceToTarget |= input.WordsSourceToTarget;
            link.WordsTargetToSource |= input.WordsTargetToSource;
            link.SentencesSourceToTarget |= input.SentencesSourceToTarget;
            link.SentencesTargetToSource |= input.SentencesTargetToSource;
            if (changed) link.UpdatedAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
            await SyncCollectionAsync(input.CollectionId, cancellationToken);
            var after = await LinkSnapshotAsync(input.CollectionId, input.QuizId, cancellationToken);
            var activeBefore = before.Cards.Where(c => c.IsActive).Select(c => c.Id).ToHashSet();
            var added = after.Cards.Count(c => c.IsActive && !activeBefore.Contains(c.Id));
            var selected = (input.WordsSourceToTarget || input.WordsTargetToSource ? await _context.Words.CountAsync(w => w.QuizId == input.QuizId, cancellationToken) : 0)
                + (input.SentencesSourceToTarget || input.SentencesTargetToSource ? await _context.QuizSentences.CountAsync(s => s.QuizId == input.QuizId, cancellationToken) : 0);
            var requestedCards = _context.AnkiCards.AsNoTracking().Where(c => c.Note.AnkiCollectionId == input.CollectionId && c.Note.QuizId == input.QuizId
                && (c.Note.ItemType == PracticeItemType.Words
                    ? c.Direction == PracticeDirection.SourceToTarget ? input.WordsSourceToTarget : input.WordsTargetToSource
                    : c.Direction == PracticeDirection.SourceToTarget ? input.SentencesSourceToTarget : input.SentencesTargetToSource));
            var included = await requestedCards.CountAsync(c => c.IsActive, cancellationToken);
            var excluded = await requestedCards.CountAsync(c => c.ExcludedFromQuizLink && !c.DirectlyIncluded, cancellationToken);
            return new AnkiAdditionResult(input.CollectionId, selected, added, Math.Max(0, included - added), [],
                changed || added > 0 ? new(before, after) : null, excluded);
        }, cancellationToken);

    private async Task CompatibleOwnedAsync(Guid collectionId, Guid quizId, string userId, CancellationToken ct)
    {
        var collection = await OwnedCollectionAsync(collectionId, userId, ct);
        var quiz = await _context.Quizzes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == quizId && q.UserId == userId, ct);
        if (collection is null || quiz is null || !Matches(collection, quiz))
            throw new AnkiValidationException("The quiz and Anki collection must belong to you and have matching learning and translation languages.");
    }

    private static IEnumerable<string> Directions(bool forward, bool reverse)
    {
        if (forward) yield return PracticeDirection.SourceToTarget;
        if (reverse) yield return PracticeDirection.TargetToSource;
    }

    private async Task<AnkiLinkSnapshot> LinkSnapshotAsync(Guid collectionId, Guid quizId, CancellationToken ct)
    {
        var link = await _context.AnkiQuizLinks.AsNoTracking().SingleOrDefaultAsync(l => l.AnkiCollectionId == collectionId && l.QuizId == quizId, ct);
        var cards = await _context.AnkiCards.AsNoTracking().Include(c => c.Note)
            .Where(c => c.Note.AnkiCollectionId == collectionId && c.Note.QuizId == quizId).OrderBy(c => c.Id).ToListAsync(ct);
        return new(collectionId, quizId, link is null ? null : AnkiLinkState.From(link), cards.Select(AnkiCardState.From).ToList());
    }

    public Task<bool> UndoCardAdditionAsync(AnkiCardChange change, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            var card = await _context.AnkiCards.Include(c => c.Note).ThenInclude(n => n.Cards)
                .SingleOrDefaultAsync(c => c.Id == change.After.Id && c.Note.Collection.UserId == userId, cancellationToken);
            if (card is null || AnkiCardState.From(card) != change.After) return false;
            if (change.Before is null)
            {
                if (await _context.AnkiReviews.AnyAsync(r => r.AnkiCardId == card.Id, cancellationToken)) return false;
                _context.AnkiCards.Remove(card);
                card.Note.Cards.Remove(card);
            }
            else
            {
                // Do not restore active membership if the source item was deleted later.
                if (change.Before.IsActive && !(card.Note.WordId is { } wordId
                    ? await _context.Words.AnyAsync(w => w.Id == wordId && w.QuizId == card.Note.QuizId, cancellationToken)
                    : await _context.QuizSentences.AnyAsync(s => s.Id == card.Note.SentenceId && s.QuizId == card.Note.QuizId, cancellationToken))) return false;
                card.DirectlyIncluded = change.Before.DirectlyIncluded;
                card.QuizLinkIncluded = change.Before.QuizLinkIncluded;
                card.ExcludedFromQuizLink = change.Before.ExcludedFromQuizLink;
                card.IsActive = change.Before.IsActive;
            }
            if (card.Note.Cards.Count == 0) _context.AnkiNotes.Remove(card.Note);
            else card.Note.IsActive = card.Note.Cards.Any(c => c.IsActive);
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<bool> UndoQuizLinkAsync(AnkiLinkChange change, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            if (!await OwnsCollectionAsync(change.After.CollectionId, userId, cancellationToken)) return false;
            var current = await LinkSnapshotAsync(change.After.CollectionId, change.After.QuizId, cancellationToken);
            if (current.Link != change.After.Link || !current.Cards.SequenceEqual(change.After.Cards)) return false;
            var beforeIds = change.Before.Cards.Select(c => c.Id).ToHashSet();
            var newIds = current.Cards.Where(c => !beforeIds.Contains(c.Id)).Select(c => c.Id).ToList();
            if (await _context.AnkiReviews.AnyAsync(r => newIds.Contains(r.AnkiCardId), cancellationToken)) return false;
            var link = await _context.AnkiQuizLinks.SingleOrDefaultAsync(l => l.AnkiCollectionId == current.CollectionId && l.QuizId == current.QuizId, cancellationToken);
            if (change.Before.Link is not { } old)
            {
                if (link is not null) _context.AnkiQuizLinks.Remove(link);
            }
            else
            {
                var collection = await OwnedCollectionAsync(current.CollectionId, userId, cancellationToken);
                var quiz = await _context.Quizzes.AsNoTracking().SingleOrDefaultAsync(q => q.Id == current.QuizId && q.UserId == userId, cancellationToken);
                if (collection is null || quiz is null || !Matches(collection, quiz)) return false;
                if (link is null)
                {
                    link = new AnkiQuizLink { Id = old.Id, AnkiCollectionId = current.CollectionId, QuizId = current.QuizId, CreatedAt = old.CreatedAt };
                    _context.AnkiQuizLinks.Add(link);
                }
                link.WordsSourceToTarget = old.WordsSourceToTarget;
                link.WordsTargetToSource = old.WordsTargetToSource;
                link.SentencesSourceToTarget = old.SentencesSourceToTarget;
                link.SentencesTargetToSource = old.SentencesTargetToSource;
                link.UpdatedAt = old.UpdatedAt;
            }
            await _context.SaveChangesAsync(cancellationToken);
            await SyncCollectionAsync(current.CollectionId, cancellationToken);
            var newCards = await _context.AnkiCards.Where(c => newIds.Contains(c.Id)).ToListAsync(cancellationToken);
            _context.AnkiCards.RemoveRange(newCards);
            await _context.SaveChangesAsync(cancellationToken);
            var emptyNotes = await _context.AnkiNotes.Where(n => n.AnkiCollectionId == current.CollectionId && n.QuizId == current.QuizId && !n.Cards.Any()).ToListAsync(cancellationToken);
            _context.AnkiNotes.RemoveRange(emptyNotes);
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);

    public Task<bool> UndoCollectionCreationAsync(AnkiCollectionState state, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            var collection = await OwnedCollectionAsync(state.Id, userId, cancellationToken);
            if (collection is null || AnkiCollectionState.From(collection) != state
                || await _context.AnkiNotes.AnyAsync(n => n.AnkiCollectionId == state.Id, cancellationToken)
                || await _context.AnkiQuizLinks.AnyAsync(l => l.AnkiCollectionId == state.Id, cancellationToken)
                || await _context.AnkiReviews.AnyAsync(r => r.AnkiCollectionId == state.Id, cancellationToken)) return false;
            _context.AnkiCollections.Remove(collection);
            await _context.SaveChangesAsync(cancellationToken);
            return true;
        }, cancellationToken);
}
