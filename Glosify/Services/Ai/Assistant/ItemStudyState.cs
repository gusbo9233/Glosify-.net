using System.Security.Cryptography;
using System.Text;
using Glosify.Data;
using Glosify.Services.Ai.Assistant.Runtime;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant;

/// <summary>Detects later Anki membership or study changes before Undo removes a created item.</summary>
internal static class ItemStudyState
{
    internal static string Key(string kind, string id) => kind + ":" + id;
    internal static string EmptyHash => Hash(Array.Empty<object>());

    internal static async Task<Dictionary<string, string>> CaptureAsync(
        GlosifyContext db, IEnumerable<(string Kind, string Id)> items, CancellationToken ct)
    {
        var requested = items.Distinct().ToList();
        var groups = requested.ToDictionary(item => Key(item.Kind, item.Id), _ => new List<object>());
        if (requested.Count == 0) return [];
        var words = requested.Where(i => i.Kind == PendingChangeKinds.AddWord).Select(i => i.Id).ToList();
        var sentences = requested.Where(i => i.Kind == PendingChangeKinds.AddSentence).Select(i => Guid.Parse(i.Id)).ToList();
        var notes = await db.AnkiNotes.AsNoTracking().Include(note => note.Cards)
            .Where(note => (note.WordId != null && words.Contains(note.WordId))
                || (note.SentenceId != null && sentences.Contains(note.SentenceId.Value)))
            .OrderBy(note => note.Id).ToListAsync(ct);
        foreach (var note in notes)
        {
            var key = note.WordId is not null ? Key(PendingChangeKinds.AddWord, note.WordId)
                : Key(PendingChangeKinds.AddSentence, note.SentenceId!.Value.ToString());
            groups[key].Add(new
            {
                note.Id, note.AnkiCollectionId, note.IsActive,
                cards = note.Cards.OrderBy(card => card.Id).Select(card => new
                {
                    card.Id, card.Direction, card.State, card.DueAt, card.Stability, card.Difficulty,
                    card.LearningStep, card.ReviewCount, card.LapseCount, card.ScheduledDays,
                    card.LastReviewedAt, card.BuriedUntil, card.IsActive, card.DirectlyIncluded,
                    card.QuizLinkIncluded, card.ExcludedFromQuizLink,
                }).ToArray(),
            });
        }
        return groups.ToDictionary(pair => pair.Key, pair => Hash(pair.Value));
    }

    private static string Hash(object value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RunJson.Write(value))));
}
