using Glosify.Data;
using Glosify.Models;
using Glosify.Models.Entities;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Learning;

/// <summary>Observed learning results, never inferred skill levels or guessed historical item identities.</summary>
public sealed class LearningInsightsService(GlosifyContext db, TimeProvider clock) : ILearningInsightsService
{
    public async Task<LearningMistakes> MistakesAsync(string userId, string targetLanguage, Guid? quizId, string? itemType,
        int days, int offset, int limit, CancellationToken cancellationToken = default)
    {
        Validate(days);
        if (offset < 0 || limit is < 1 or > 100) throw new ArgumentException("Use a nonnegative offset and a limit between 1 and 100.");
        if (itemType is not (null or PracticeItemType.Words or PracticeItemType.Sentences)) throw new ArgumentException("Choose words or sentences.");
        var (quizzes, attempts, reviews, since) = await ReadAsync(userId, targetLanguage, quizId, days, cancellationToken);
        var observations = new List<Observation>();
        foreach (var attempt in attempts)
        foreach (var item in attempt.Items.Where(i => i.ItemId != null && !i.IsSkipped))
            observations.Add(new(attempt.QuizId, PracticeItemType.Normalize(attempt.PracticeItemType), NormalizeItemId(attempt.PracticeItemType, item.ItemId!),
                !item.IsCorrect, attempt.CompletedAt, false));
        observations.AddRange(reviews.Select(r => new Observation(r.QuizId, r.ItemType, r.ItemId,
            r.Rating == "again", r.At, true)));
        var groups = observations.Where(o => itemType is null || o.Kind == itemType)
            .GroupBy(o => (o.QuizId, o.Kind, o.Id)).Where(g => g.Any(o => o.Mistake)).ToList();
        var wordIds = groups.Where(g => g.Key.Kind == PracticeItemType.Words).Select(g => g.Key.Id).Distinct().ToArray();
        var sentenceIds = groups.Where(g => g.Key.Kind == PracticeItemType.Sentences).Select(g => Guid.TryParse(g.Key.Id, out var id) ? id : Guid.Empty).Distinct().ToArray();
        var quizIds = quizzes.Keys.ToList();
        var words = await db.Words.AsNoTracking().Where(w => quizIds.Contains(w.QuizId) && wordIds.Contains(w.Id)).ToListAsync(cancellationToken);
        var sentences = await db.QuizSentences.AsNoTracking().Where(s => quizIds.Contains(s.QuizId) && sentenceIds.Contains(s.Id)).ToListAsync(cancellationToken);
        var content = words.ToDictionary(w => (w.QuizId, PracticeItemType.Words, w.Id), w => (w.Lemma, w.Translation));
        foreach (var sentence in sentences) content[(sentence.QuizId, PracticeItemType.Sentences, sentence.Id.ToString())] = (sentence.Text, sentence.Translation);
        var ranked = new List<LearningItem>();
        foreach (var group in groups)
        {
            var key = group.Key;
            // Deleted/moved content and unidentifiable legacy history never become actionable ids.
            if (!content.TryGetValue(key, out var text)) continue;
            var quiz = quizzes[key.QuizId];
            ranked.Add(new(quiz.Id, quiz.Name, quiz.SourceLanguage, QuizLanguageCatalog.TargetName(quiz.TargetLanguage, quiz.Language), key.Kind, key.Id, text.Item1, text.Item2,
                group.Count(o => o.Mistake), group.Count(), group.Where(o => o.Mistake).Max(o => o.At),
                group.Count(o => o.Mistake && !o.Anki), group.Count(o => o.Mistake && o.Anki)));
        }
        var ordered = ranked.OrderByDescending(i => i.Mistakes).ThenByDescending(i => (double)i.Mistakes / i.Observations)
            .ThenByDescending(i => i.LastMistakeAt).ThenBy(i => i.QuizId).ThenBy(i => i.ItemId, StringComparer.Ordinal).ToList();
        var rows = ordered.Skip(offset).Take(limit).ToList();
        return new(rows, ordered.Count, offset + rows.Count < ordered.Count ? offset + rows.Count : null,
            attempts.Sum(a => a.Items.Count(i => i.ItemId is null)), since, attempts.Count(a => a.TotalItems > 0 && a.Items.Count == 0));
    }

    public async Task<LearningProgress> ProgressAsync(string userId, string targetLanguage, Guid? quizId, int days, CancellationToken cancellationToken = default)
    {
        Validate(days);
        var (_, attempts, reviews, since) = await ReadAsync(userId, targetLanguage, quizId, days, cancellationToken);
        var correct = attempts.Sum(a => a.CorrectCount);
        var incorrect = attempts.Sum(a => a.IncorrectCount);
        var again = reviews.Count(r => r.Rating == "again");
        return new(since, attempts.Count, correct, incorrect, attempts.Sum(a => a.SkippedCount),
            correct + incorrect == 0 ? null : Math.Round(100d * correct / (correct + incorrect), 1),
            reviews.Count, again, reviews.Count == 0 ? null : Math.Round(100d * (reviews.Count - again) / reviews.Count, 1),
            attempts.Sum(a => a.Items.Count(i => i.ItemId is null)), attempts.Count(a => a.TotalItems > 0 && a.Items.Count == 0));
    }

    private async Task<(Dictionary<Guid, Quiz> Quizzes, List<QuizAttempt> Attempts, List<Review> Reviews, DateTimeOffset Since)> ReadAsync(
        string userId, string targetLanguage, Guid? quizId, int days, CancellationToken ct)
    {
        var language = QuizLanguageCatalog.Find(targetLanguage)?.Name ?? throw new ArgumentException("Select a learning language first.");
        var quizzes = await db.Quizzes.AsNoTracking().Where(q => q.UserId == userId && (q.TargetLanguage == language || ((q.TargetLanguage == null || q.TargetLanguage.Trim() == "") && q.Language == language)) && (quizId == null || q.Id == quizId)).ToDictionaryAsync(q => q.Id, ct);
        if (quizId.HasValue && quizzes.Count == 0) throw new ArgumentException("Quiz not found for the selected language.");
        var ids = quizzes.Keys.ToList();
        var now = clock.GetUtcNow();
        var since = now.AddDays(-days);
        var attemptsQuery = db.QuizAttempts.AsNoTracking().Include(a => a.Items).Where(a => a.UserId == userId && ids.Contains(a.QuizId));
        var reviewsQuery = db.AnkiReviews.AsNoTracking().Where(r => r.Collection.UserId == userId && ids.Contains(r.Card.Note.QuizId));
        // SQLite's native DateTimeOffset lacks range comparisons; production applies ranges in SQL.
        var sqlite = db.Database.ProviderName?.Contains("Sqlite", StringComparison.Ordinal) == true;
        var attempts = sqlite
            ? (await attemptsQuery.ToListAsync(ct)).Where(a => a.CompletedAt >= since && a.CompletedAt <= now).ToList()
            : await attemptsQuery.Where(a => a.CompletedAt >= since && a.CompletedAt <= now).ToListAsync(ct);
        if (!sqlite) reviewsQuery = reviewsQuery.Where(r => r.ReviewedAt >= since && r.ReviewedAt <= now);
        var reviewRows = await reviewsQuery.Select(r => new { r.Card.Note.QuizId, r.Card.Note.ItemType,
            r.Card.Note.WordId, r.Card.Note.SentenceId, r.Rating, r.ReviewedAt }).ToListAsync(ct);
        var reviews = reviewRows.Where(r => r.ReviewedAt >= since && r.ReviewedAt <= now)
            .Select(r => new Review(r.QuizId, r.ItemType, r.WordId ?? r.SentenceId?.ToString() ?? "", r.Rating, r.ReviewedAt)).ToList();
        return (quizzes, attempts, reviews, since);
    }

    // Practice uses N-format GUIDs; Anki uses D-format. Normalize before grouping so
    // both histories contribute to one sentence and already-recorded attempts keep working.
    private static string NormalizeItemId(string? kind, string id) =>
        PracticeItemType.IsSentences(kind) && Guid.TryParse(id, out var sentenceId) ? sentenceId.ToString() : id;

    private static void Validate(int days)
    {
        if (days is < 1 or > 365) throw new ArgumentException("Choose a history window between 1 and 365 days.");
    }
    private sealed record Observation(Guid QuizId, string Kind, string Id, bool Mistake, DateTimeOffset At, bool Anki);
    private sealed record Review(Guid QuizId, string ItemType, string ItemId, string Rating, DateTimeOffset At);
}
