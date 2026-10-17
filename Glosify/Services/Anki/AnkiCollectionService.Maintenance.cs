using Glosify.Models.Entities;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Anki;

public sealed record RemoveAnkiCardsInput(Guid CollectionId, IReadOnlyList<AnkiCardState> ExpectedCards);

public sealed partial class AnkiCollectionService
{
    public async Task<IReadOnlyList<AnkiCardState>> ReadCardStatesAsync(Guid collectionId, IReadOnlyList<Guid> cardIds,
        string userId, CancellationToken cancellationToken = default)
    {
        if (cardIds.Count is < 1 or > 100 || !await OwnsCollectionAsync(collectionId, userId, cancellationToken))
            throw new AnkiValidationException("Choose 1 to 100 cards from an owned Anki collection.");
        var cards = await _context.AnkiCards.AsNoTracking().Include(c => c.Note)
            .Where(c => c.Note.AnkiCollectionId == collectionId && cardIds.Contains(c.Id)).ToListAsync(cancellationToken);
        if (cards.Count != cardIds.Distinct().Count()) throw new AnkiValidationException("One or more cards were not found in this collection.");
        return cards.Select(AnkiCardState.From).ToList();
    }

    public async Task<AnkiLinkSnapshot> ReadLinkStateAsync(Guid collectionId, Guid quizId, string userId, CancellationToken cancellationToken = default)
    {
        if (!await OwnsCollectionAsync(collectionId, userId, cancellationToken)) throw new AnkiValidationException("Anki collection not found.");
        return await LinkSnapshotAsync(collectionId, quizId, cancellationToken);
    }

    public Task<IReadOnlyList<AnkiCardChange>> RemoveCardsWithUndoAsync(RemoveAnkiCardsInput input, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync<IReadOnlyList<AnkiCardChange>>(async () =>
        {
            var ids = input.ExpectedCards.Select(c => c.Id).ToArray();
            var current = await ReadCardStatesAsync(input.CollectionId, ids, userId, cancellationToken);
            if (current.Count != input.ExpectedCards.Count || current.Any(c => !input.ExpectedCards.Contains(c)))
                throw new AnkiValidationException("The selected cards changed. Read the collection again before removing them.");
            var cards = await _context.AnkiCards.Include(c => c.Note).ThenInclude(n => n.Cards).Where(c => ids.Contains(c.Id)).ToListAsync(cancellationToken);
            var changes = new List<AnkiCardChange>();
            foreach (var card in cards.Where(c => c.IsActive || c.DirectlyIncluded || c.QuizLinkIncluded && !c.ExcludedFromQuizLink))
            {
                var before = AnkiCardState.From(card);
                card.DirectlyIncluded = false;
                card.ExcludedFromQuizLink = card.QuizLinkIncluded;
                card.IsActive = false;
                changes.Add(new(before, AnkiCardState.From(card)));
            }
            foreach (var note in cards.Select(c => c.Note).DistinctBy(n => n.Id)) note.IsActive = note.Cards.Any(c => c.IsActive);
            await _context.SaveChangesAsync(cancellationToken);
            return changes;
        }, cancellationToken);

    public Task<AnkiLinkChange> UnlinkWithUndoAsync(AnkiLinkSnapshot expected, string userId, CancellationToken cancellationToken = default) =>
        ExecuteAtomicallyAsync(async () =>
        {
            var current = await ReadLinkStateAsync(expected.CollectionId, expected.QuizId, userId, cancellationToken);
            if (current.Link is null || current.Link != expected.Link || !current.Cards.SequenceEqual(expected.Cards))
                throw new AnkiValidationException("The quiz link or its cards changed. Read the collection again before unlinking.");
            await RemoveQuizAsync(expected.CollectionId, expected.QuizId, userId, cancellationToken);
            return new AnkiLinkChange(current, await LinkSnapshotAsync(expected.CollectionId, expected.QuizId, cancellationToken));
        }, cancellationToken);
}
