using System.Text;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Ai.Assistant.Tools;
using Glosify.Services.Ai.Generation;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Resolves what a run is about when it starts, and turns that into tool context and the
/// per-step notes the model sees.
/// </summary>
internal sealed class AssistantRunContext(
    GlosifyContext db,
    AssistantContextResolver resolver,
    AssistantIntentResolver intents)
{
    internal sealed record Resolved(RunFacts Facts, string ContextNote, Guid? TranscriptId, Guid? BookDocumentId);

    /// <summary>
    /// Resolves the request's quiz, languages, and reading material. Ownership failures throw,
    /// so a run never acts on another user's content.
    /// </summary>
    public async Task<Resolved> ResolveAsync(
        AssistantRun run,
        AssistantRunInput input,
        AssistantThread thread,
        CancellationToken cancellationToken)
    {
        var userId = run.UserId;
        var quiz = await resolver.ResolveQuizAsync(input.ContextQuizId, userId, cancellationToken);
        var currentLanguage = quiz?.TargetLanguage ?? await resolver.ResolveLanguageAsync(userId, cancellationToken);
        var mode = QuizLanguageCatalog.IsFreestyle(currentLanguage) ? AssistantMode.Freestyle : AssistantMode.Language;
        var focused = quiz is null || string.IsNullOrWhiteSpace(input.FocusedWordId)
            ? null
            : await db.Words.AsNoTracking()
                .FirstOrDefaultAsync(word => word.QuizId == quiz.Id && word.Id == input.FocusedWordId, cancellationToken);
        var page = input.DocumentContext is null
            ? null
            : await resolver.ResolveDocumentPageAsync(input.DocumentContext, userId, cancellationToken);
        var transcript = mode == AssistantMode.Freestyle
            ? null
            : await resolver.ResolveTranscriptAsync(input.TranscriptId, input.TranscriptContext, userId, cancellationToken);
        var book = await resolver.ResolveBookAsync(input.BookDocumentId, userId, cancellationToken);
        var sourceLanguage = mode == AssistantMode.Freestyle
            ? QuizLanguageCatalog.FreestyleName
            : resolver.ResolveSourceLanguage(quiz);
        var replyLanguage = await resolver.ResolveReplyLanguageAsync(userId, thread, cancellationToken);
        var sourceLines = input.Message.Length > AssistantPrompts.InlineSourceCharacters
            ? new SourceText(input.Message).LineCount
            : (int?)null;
        int? words = null, sentences = null;
        if (quiz is not null)
        {
            words = await db.Words.CountAsync(word => word.QuizId == quiz.Id, cancellationToken);
            sentences = await db.QuizSentences.CountAsync(sentence => sentence.QuizId == quiz.Id, cancellationToken);
        }

        var focusLabel = focused is null ? null : $"\"{focused.Lemma}\" → \"{focused.Translation}\"";
        var facts = new RunFacts(
            mode,
            (mode, quiz is null) switch
            {
                (AssistantMode.Freestyle, false) => AssistantAgentProfile.FreestyleQuizAssistant,
                (AssistantMode.Freestyle, true) => AssistantAgentProfile.FreestyleLibrarian,
                (_, false) => AssistantAgentProfile.QuizAssistant,
                _ => AssistantAgentProfile.Librarian,
            },
            quiz?.Id,
            currentLanguage,
            await resolver.ResolveLanguageCodeAsync(userId, cancellationToken),
            sourceLanguage,
            replyLanguage,
            focused?.Id,
            focusLabel,
            transcript?.Id,
            book?.Id,
            intents.Resolve(input.Message).ContentKind,
            sourceLines);
        var note = AssistantPrompts.ContextNote(new AssistantContextFacts(
            mode,
            currentLanguage,
            sourceLanguage,
            replyLanguage,
            quiz?.Id,
            quiz?.Name,
            quiz is null ? null : $"{quiz.TargetLanguage} with translations in {quiz.SourceLanguage}",
            words,
            sentences,
            focused?.Id,
            focusLabel,
            book,
            transcript,
            page,
            sourceLines));
        return new Resolved(facts, note, transcript?.Id, book?.Id);
    }

    public ToolContext Tools(AssistantRun run, RunFacts facts)
    {
        SourceText? source = null;
        if (facts.SourceLines is not null)
        {
            source = new SourceText(RunJson.Read<AssistantRunInput>(run.RequestJson).Message);
        }

        return new ToolContext
        {
            UserId = run.UserId,
            ThreadId = run.ThreadId,
            UserMessageId = run.UserMessageId,
            Mode = facts.Mode,
            QuizId = facts.QuizId,
            TargetLanguage = facts.TargetLanguage,
            TargetLanguageCode = facts.TargetLanguageCode,
            SourceLanguage = facts.SourceLanguage,
            FocusedWordId = facts.FocusedWordId,
            FocusedWordLabel = facts.FocusedWordLabel,
            TranscriptId = facts.TranscriptId,
            BookDocumentId = facts.BookDocumentId,
            RequestedContentKind = facts.RequestedContentKind,
            Source = source,
        };
    }

    /// <summary>Re-reads the requested content type after the user steers the run.</summary>
    public RunFacts Steer(RunFacts facts, string message) =>
        facts with { RequestedContentKind = intents.Resolve(message).ContentKind };

    /// <summary>
    /// Volatile per-step state for the end of the request: progress, unread source, recent
    /// failures. Kept out of the system prompt and stored history so the cached prefix holds.
    /// </summary>
    public static string? RunNote(
        AssistantRun run,
        AssistantRunState state,
        IReadOnlyList<AssistantPlanItem> plan,
        IReadOnlyList<AssistantRunArtifact> created,
        bool finalCall)
    {
        var note = new StringBuilder();
        if (plan.Count > 0)
        {
            note.AppendLine("- Your checklist: " + string.Join("; ", plan.Select(item => $"[{item.Status}] {item.Text}")));
        }

        if (state.Facts?.SourceLines is int lines && UnreadSource(state, lines) is { } unread)
        {
            note.AppendLine($"- Source text: {lines} lines. Not yet read: lines {unread}.");
        }

        if (run.SavedChanges > 0)
        {
            note.AppendLine($"- Saved so far for this request: {QuizContent.Count(run.SavedChanges, "change")}.");
        }

        foreach (var quiz in created)
        {
            note.AppendLine($"- You created quiz \"{quiz.Name}\" (id {quiz.Id}) in this request. Add to it with add_items and this quiz_id.");
        }

        if (state.NoProgress >= 2)
        {
            note.AppendLine("- Your last calls made no progress or reread unchanged data. Use the source and item IDs already returned to perform the requested changes in batches now; do not restart the review or reread the same pages.");
            if (state.RecentErrors.Count > 0)
                note.AppendLine("- Latest error: " + state.RecentErrors[^1]);
        }

        if (finalCall)
        {
            note.AppendLine();
            note.AppendLine(AssistantPrompts.MaxSteps);
        }

        return note.Length == 0 ? null : "Run state (from the app, not the user):\n" + note.ToString().TrimEnd();
    }

    /// <summary>Unread line ranges as text, such as "41–120, 300–812", or null when all are read.</summary>
    public static string? UnreadSource(AssistantRunState state, int lines)
    {
        var gaps = new List<string>();
        // The first lines were shown in the message itself.
        var next = Math.Min(lines, AssistantPrompts.SourcePreviewLines) + 1;
        foreach (var range in state.SourceRead.OrderBy(range => range[0]))
        {
            if (range[0] > next)
            {
                gaps.Add(Gap(next, range[0] - 1));
            }

            next = Math.Max(next, range[1] + 1);
        }

        if (next <= lines)
        {
            gaps.Add(Gap(next, lines));
        }

        return gaps.Count == 0 ? null : string.Join(", ", gaps);

        static string Gap(int from, int to) => from == to ? $"{from}" : $"{from}–{to}";
    }

    public static void RecordRead(AssistantRunState state, int from, int to)
    {
        var ranges = state.SourceRead.Append([from, to]).OrderBy(range => range[0]).ToList();
        var merged = new List<int[]>();
        foreach (var range in ranges)
        {
            if (merged.Count > 0 && range[0] <= merged[^1][1] + 1)
            {
                merged[^1][1] = Math.Max(merged[^1][1], range[1]);
                continue;
            }

            merged.Add([range[0], range[1]]);
        }

        state.SourceRead = merged;
    }
}
