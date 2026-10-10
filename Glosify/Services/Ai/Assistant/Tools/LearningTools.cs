using System.ComponentModel;
using Glosify.Data;
using Glosify.Models;
using Glosify.Services.Anki;
using Glosify.Services.Language;
using Glosify.Services.Learning;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Tools;

internal sealed record LearningMistakesArgs(
    [property: Description("Null for all owned quizzes in the selected learning language.")] Guid? QuizId = null,
    QuizItemKind? Kind = null, int? Days = null, int? Offset = null, int? Limit = null);
internal sealed record LearningProgressArgs(Guid? QuizId = null, int? Days = null,
    [property: Description("Optional Anki collection id for its separate 30-day retention and 14-day due forecast.")] Guid? CollectionId = null);
internal enum StudyMode { Auto, Anki, Typing, Flashcards }
internal sealed record PrepareStudyArgs(StudyMode? Mode = null, int? Minutes = null, Guid? QuizId = null,
    Guid? CollectionId = null, QuizItemKind? Kind = null,
    [property: Description("Optional exact word ids within one quiz. For a sentence subset create a focused quiz first.")] IReadOnlyList<string>? ItemIds = null);

internal sealed class GetLearningMistakesTool(ILearningInsightsService insights) : AssistantTool<LearningMistakesArgs>
{
    public override string Name => "get_learning_mistakes";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Rank current quiz items by recorded quiz mistakes plus Anki Again ratings, then mistake rate and recency. Defaults: last 30 days, first 20 items; days 1-365, limit 1-100. Returns exact ids, current text, translations, quiz and language pair for targeted practice or add_anki_items. Skips and unidentifiable legacy history are excluded; this is observed difficulty, not a proficiency assessment.";
    protected override async Task<ToolResult> RunAsync(LearningMistakesArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            var result = await insights.MistakesAsync(context.UserId, context.TargetLanguage ?? "", args.QuizId,
                args.Kind is null ? null : args.Kind == QuizItemKind.Words ? PracticeItemType.Words : PracticeItemType.Sentences,
                args.Days ?? 30, args.Offset ?? 0, args.Limit ?? 20, ct);
            return ToolResult.Ok("Read recorded learning mistakes", result);
        }
        catch (ArgumentException ex) { return ToolResult.Fail("Could not read learning mistakes", ex.Message); }
    }
}

internal sealed class GetLearningProgressTool(ILearningInsightsService insights, IAnkiStatisticsService statistics, GlosifyContext db) : AssistantTool<LearningProgressArgs>
{
    public override string Name => "get_learning_progress";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Read quiz accuracy (excluding skips) and Anki retention (non-Again reviews), last 30 days by default, days 1-365. Null rates mean no observations. Optional collection_id adds that collection's separate 30-day stats and 14-day due forecast in its timezone, regardless of quiz_id. Due count includes overdue; forecast does not. Reading never synchronizes or changes cards.";
    protected override async Task<ToolResult> RunAsync(LearningProgressArgs args, ToolContext context, CancellationToken ct)
    {
        try
        {
            object? collectionProgress = null;
            if (args.CollectionId is Guid id)
            {
                var collection = await AnkiTools.OwnedAsync(db, id.ToString(), context.UserId, ct);
                if (collection is null) return ToolResult.Fail("Could not read progress", "Anki collection not found.");
                var stats = await statistics.ReadSnapshotAsync(id, context.UserId, ct);
                if (stats is not null) collectionProgress = new { collection_id = id, collection.Name, collection.TimeZoneId,
                    stats.Counts, stats.ReviewsLast30Days, retention_percent = stats.ReviewsLast30Days == 0 ? (double?)null : stats.RetentionPercent,
                    stats.ReviewActivity, stats.DueForecast };
            }
            return ToolResult.Ok("Read learning progress", new { progress = await insights.ProgressAsync(context.UserId,
                context.TargetLanguage ?? "", args.QuizId, args.Days ?? 30, ct), collection_progress = collectionProgress });
        }
        catch (ArgumentException ex) { return ToolResult.Fail("Could not read progress", ex.Message); }
    }
}

internal sealed class PrepareStudySessionTool(GlosifyContext db, IAnkiCollectionService anki) : AssistantTool<PrepareStudyArgs>
{
    public override string Name => "prepare_study_session";
    public override AssistantToolKind Kind => AssistantToolKind.Read;
    public override bool Supports(AssistantMode mode) => mode == AssistantMode.Language;
    protected override string Describe(AssistantMode mode) => "Return a direct local study link with a suggested item count for a time budget (1-60 minutes, default 10). Auto prefers due Anki cards, then new Anki cards, then the current quiz. Estimates two items per minute, not an enforced timer. Does not begin a session or rate cards. Anki uses its normal daily limits; selected word ids require typing/flashcards in one quiz.";
    protected override async Task<ToolResult> RunAsync(PrepareStudyArgs args, ToolContext context, CancellationToken ct)
    {
        var minutes = args.Minutes ?? 10;
        var mode = args.Mode ?? StudyMode.Auto;
        var ids = args.ItemIds?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        if (minutes is < 1 or > 60 || ids.Length > 100 || ids.Any(string.IsNullOrWhiteSpace))
            return ToolResult.Fail("Could not prepare study", "Choose 1-60 minutes and at most 100 valid word ids.");
        if ((mode == StudyMode.Anki || args.CollectionId.HasValue) && args.Kind is not null)
            return ToolResult.Fail("Could not prepare study", "Anki studies the collection’s scheduled cards. Choose typing or flashcards to restrict practice to words or sentences.");
        if (args.CollectionId.HasValue && (args.QuizId.HasValue || ids.Length > 0 || mode is StudyMode.Typing or StudyMode.Flashcards)
            || mode == StudyMode.Anki && (args.QuizId.HasValue || ids.Length > 0))
            return ToolResult.Fail("Could not prepare study", "Choose either an Anki collection or a quiz practice session.");
        var language = QuizLanguageCatalog.Find(context.TargetLanguage ?? "")?.Name;
        var suggested = Math.Min(100, minutes * 2);
        if (mode == StudyMode.Anki || args.CollectionId.HasValue || mode == StudyMode.Auto && !args.QuizId.HasValue && ids.Length == 0 && args.Kind is null)
        {
            var collections = await db.AnkiCollections.AsNoTracking().Where(c => c.UserId == context.UserId && c.TargetLanguage == language
                && (args.CollectionId == null || c.Id == args.CollectionId)).OrderBy(c => c.Name).ThenBy(c => c.Id).ToListAsync(ct);
            var choices = new List<(Guid Id, string Name, AnkiCollectionCounts Counts)>();
            foreach (var collection in collections)
            {
                var counts = await anki.ReadCountsAsync(collection.Id, context.UserId, ct);
                if (counts is not null) choices.Add((collection.Id, collection.Name, counts));
            }
            var selected = choices.OrderByDescending(c => c.Counts.Due > 0).ThenByDescending(c => c.Counts.Due)
                .ThenByDescending(c => c.Counts.New).FirstOrDefault();
            if (selected != default && (selected.Counts.Due + selected.Counts.New > 0 || args.CollectionId.HasValue || mode == StudyMode.Anki))
                return ToolResult.Ok("Prepared Anki study link", new { mode = "anki", selected.Name,
                    url = $"/Anki/Study/{selected.Id}", minutes, suggested_items = Math.Min(suggested, selected.Counts.Due + selected.Counts.New),
                    time_is_estimate = true, daily_limits_apply = true, counts = selected.Counts });
            if (mode == StudyMode.Anki || args.CollectionId.HasValue)
                return ToolResult.Fail("Could not prepare Anki study", "No Anki collection found for the selected language.");
        }
        var sentences = args.Kind == QuizItemKind.Sentences;
        var quizId = args.QuizId ?? context.QuizId;
        var quizzes = db.Quizzes.AsNoTracking().Where(q => q.UserId == context.UserId && (q.TargetLanguage == language
            || ((q.TargetLanguage == null || q.TargetLanguage.Trim() == "") && q.Language == language)));
        var quiz = quizId.HasValue ? await quizzes.SingleOrDefaultAsync(q => q.Id == quizId, ct)
            : await quizzes.Where(q => sentences
                ? db.QuizSentences.Any(s => s.QuizId == q.Id)
                : db.Words.Any(w => w.QuizId == q.Id)).OrderBy(q => q.Name).ThenBy(q => q.Id).FirstOrDefaultAsync(ct);
        if (quiz is null) return ToolResult.Fail("Could not prepare study", "Choose an owned quiz in the selected language first.");
        if (sentences && ids.Length > 0) return ToolResult.Fail("Could not prepare study", "Create a focused quiz for a sentence selection, then open that quiz.");
        var count = sentences ? await db.QuizSentences.CountAsync(s => s.QuizId == quiz.Id, ct)
            : await db.Words.CountAsync(w => w.QuizId == quiz.Id && (ids.Length == 0 || ids.Contains(w.Id)), ct);
        if (ids.Length > 0 && count != ids.Length) return ToolResult.Fail("Could not prepare study", "One or more word ids were not found in this quiz.");
        if (count == 0) return ToolResult.Fail("Could not prepare study", "This quiz has no items of the selected kind.");
        var controller = mode == StudyMode.Flashcards ? "FlashcardQuiz" : "TypingQuiz";
        var kind = sentences ? PracticeItemType.Sentences : PracticeItemType.Words;
        var itemCount = ids.Length > 0 ? ids.Length : Math.Min(count, suggested);
        var url = $"/{controller}?id={quiz.Id}&wordCount={itemCount}&practiceItemType={kind}";
        if (ids.Length > 0) url += "&selectedWordIds=" + Uri.EscapeDataString(string.Join(",", ids));
        return ToolResult.Ok("Prepared quiz study link", new { mode = controller == "TypingQuiz" ? "typing" : "flashcards",
            quiz_id = quiz.Id, quiz.Name, url, minutes, suggested_items = itemCount, time_is_estimate = true }, quiz.Id);
    }
}
