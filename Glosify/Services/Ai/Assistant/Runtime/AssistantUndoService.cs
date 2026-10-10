using System.Data;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Anki;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Ai.Assistant.Runtime;

/// <summary>
/// Reverts everything a finished run saved, newest first.
/// </summary>
/// <remarks>
/// Undo is what makes saving immediately safe. Each journaled change is reverted only while
/// the item still looks the way the run left it; anything the user edited afterwards is kept,
/// so Undo never destroys the user's own work. The conversation gets a note, so the model does
/// not describe the undone changes as still in place.
/// </remarks>
internal sealed class AssistantUndoService(
    GlosifyContext db,
    IQuizService quizzes,
    ICollectionService collections,
    IAnkiCollectionService anki,
    AssistantRunStore store,
    AssistantRunSignals signals)
{
    public async Task<AssistantUndoResult> UndoAsync(Guid runId, string userId, CancellationToken cancellationToken)
    {
        var strategy = db.Database.CreateExecutionStrategy();
        var result = await strategy.ExecuteAsync(async () =>
        {
            db.ChangeTracker.Clear();
            await using var transaction = db.Database.IsRelational()
                ? await db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken)
                : null;
            var run = await store.OwnedAsync(runId, userId, cancellationToken);
            if (!AssistantRunStatus.IsTerminal(run.Status))
            {
                throw new AssistantRunConflictException("Wait for the request to finish, or stop it, before undoing its changes.");
            }

            if (run.UndoneAt is not null)
            {
                return new AssistantUndoResult(0, 0);
            }

            var changes = await db.AssistantChanges
                .Where(change => change.RunId == run.Id && change.Status == AssistantChangeStatus.Applied)
                .OrderByDescending(change => change.Sequence)
                .ToListAsync(cancellationToken);
            var undone = 0;
            var touched = new HashSet<Guid>();
            foreach (var change in changes)
            {
                var reverted = await RevertAsync(change, userId, cancellationToken);
                change.Status = reverted ? AssistantChangeStatus.Undone : AssistantChangeStatus.Kept;
                undone += reverted ? 1 : 0;
                if (reverted && change.QuizId is Guid quizId && change.EntityType is AppliedEntityTypes.Word or AppliedEntityTypes.Sentence)
                {
                    touched.Add(quizId);
                }

                // Deleting a quiz saves on its own; keep the journal in step with it.
                await db.SaveChangesAsync(cancellationToken);
            }

            var kept = changes.Count - undone;
            run.UndoneAt = store.Now;
            run.Revision++;
            if (run.CurrentMessageId is Guid messageId)
            {
                var state = RunJson.Read<AssistantRunState>(run.StateJson);
                db.AssistantParts.Add(new AssistantPart
                {
                    Id = Guid.NewGuid(),
                    MessageId = messageId,
                    RunId = run.Id,
                    Sequence = await store.NextPartSequenceAsync(messageId, cancellationToken),
                    Step = state.Step + 2,
                    Type = AssistantPartTypes.Reminder,
                    Text = kept == 0
                        ? "The user undid every change from this request, so none of them are in place any more."
                        : $"The user undid this request's changes: {undone} reverted, {kept} kept because the user had changed those items afterwards.",
                    CreatedAt = store.Now,
                });
            }

            await db.SaveChangesAsync(cancellationToken);
            foreach (var quizId in touched)
            {
                if (await db.Quizzes.AnyAsync(quiz => quiz.Id == quizId, cancellationToken))
                {
                    await anki.SyncQuizAsync(quizId, cancellationToken);
                }
            }

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken);
            }

            return new AssistantUndoResult(undone, kept);
        });
        AssistantRunTelemetry.Undos.Add(1);
        signals.Notify(runId);
        return result;
    }

    private async Task<bool> RevertAsync(AssistantChange change, string userId, CancellationToken cancellationToken)
    {
        var before = Read(change.BeforeJson);
        var after = Read(change.AfterJson);
        switch (change.Kind)
        {
            case PendingChangeKinds.CreateAnkiCollection:
                return await anki.UndoCollectionCreationAsync(RunJson.Read<AnkiCollectionState>(change.AfterJson!), userId, cancellationToken);
            case PendingChangeKinds.RenameAnkiCollection:
                return await anki.UndoRenameAsync(new(RunJson.Read<AnkiRenameState>(change.BeforeJson!), RunJson.Read<AnkiRenameState>(change.AfterJson!)), userId, cancellationToken);
            case PendingChangeKinds.RemoveAnkiCards:
            case PendingChangeKinds.AddAnkiItems:
                return await anki.UndoCardAdditionAsync(new(
                    change.BeforeJson is null ? null : RunJson.Read<AnkiCardState>(change.BeforeJson),
                    RunJson.Read<AnkiCardState>(change.AfterJson!)), userId, cancellationToken);
            case PendingChangeKinds.UnlinkAnkiQuiz:
            case PendingChangeKinds.LinkAnkiQuiz:
                return await anki.UndoQuizLinkAsync(new(RunJson.Read<AnkiLinkSnapshot>(change.BeforeJson!),
                    RunJson.Read<AnkiLinkSnapshot>(change.AfterJson!)), userId, cancellationToken);
            case PendingChangeKinds.AddWord:
            {
                var word = await OwnedWordAsync(change, userId, cancellationToken);
                if (word is null || !Matches(after, "word", word.Lemma) || !Matches(after, "translation", word.Translation))
                {
                    return false;
                }

                db.Words.Remove(word);
                return true;
            }

            case PendingChangeKinds.AddSentence:
            {
                var sentence = await OwnedSentenceAsync(change, userId, cancellationToken);
                if (sentence is null || !Matches(after, "text", sentence.Text) || !Matches(after, "translation", sentence.Translation))
                {
                    return false;
                }

                db.QuizSentences.Remove(sentence);
                return true;
            }

            case PendingChangeKinds.EditWord:
            {
                var word = await OwnedWordAsync(change, userId, cancellationToken);
                if (word is null || !Matches(after, "word", word.Lemma) || !Matches(after, "translation", word.Translation))
                {
                    return false;
                }

                var original = Value(before, "word") ?? word.Lemma;
                if (await db.Words.AnyAsync(other => other.QuizId == word.QuizId && other.Id != word.Id && other.Lemma == original, cancellationToken))
                {
                    return false;
                }

                word.Lemma = original;
                word.Translation = Value(before, "translation") ?? word.Translation;
                return true;
            }

            case PendingChangeKinds.EditSentence:
            {
                var sentence = await OwnedSentenceAsync(change, userId, cancellationToken);
                if (sentence is null || !Matches(after, "text", sentence.Text) || !Matches(after, "translation", sentence.Translation))
                {
                    return false;
                }

                var original = Value(before, "text") ?? sentence.Text;
                if (await db.QuizSentences.AnyAsync(other => other.QuizId == sentence.QuizId && other.Id != sentence.Id && other.Text == original, cancellationToken))
                {
                    return false;
                }

                sentence.Text = original;
                sentence.Translation = Value(before, "translation") ?? sentence.Translation;
                return true;
            }

            case PendingChangeKinds.DeleteWord:
            {
                if (change.QuizId is not Guid quizId || !await OwnsQuizAsync(quizId, userId, cancellationToken)
                    || await db.Words.AnyAsync(word => word.Id == change.EntityId, cancellationToken))
                {
                    return false;
                }

                var lemma = Value(before, "word");
                if (lemma is null || await db.Words.AnyAsync(word => word.QuizId == quizId && word.Lemma == lemma, cancellationToken))
                {
                    return false;
                }

                db.Words.Add(new Word { Id = change.EntityId, QuizId = quizId, Lemma = lemma, Translation = Value(before, "translation") ?? string.Empty });
                return true;
            }

            case PendingChangeKinds.DeleteSentence:
            {
                if (change.QuizId is not Guid quizId || !await OwnsQuizAsync(quizId, userId, cancellationToken)
                    || !Guid.TryParse(change.EntityId, out var id)
                    || await db.QuizSentences.AnyAsync(sentence => sentence.Id == id, cancellationToken))
                {
                    return false;
                }

                var text = Value(before, "text");
                if (text is null || await db.QuizSentences.AnyAsync(sentence => sentence.QuizId == quizId && sentence.Text == text, cancellationToken))
                {
                    return false;
                }

                db.QuizSentences.Add(new QuizSentence
                {
                    Id = id,
                    QuizId = quizId,
                    Text = text,
                    Translation = Value(before, "translation") ?? string.Empty,
                    CreatedAt = before?.TryGetProperty("created_at", out var created) == true && created.TryGetDateTimeOffset(out var at) ? at : DateTimeOffset.UtcNow,
                });
                return true;
            }

            case PendingChangeKinds.CreateQuiz:
            {
                // Only an empty quiz goes: its own additions were reverted first, and anything
                // left is content the user added, which must survive.
                if (!Guid.TryParse(change.EntityId, out var quizId)
                    || await db.Words.AnyAsync(word => word.QuizId == quizId, cancellationToken)
                    || await db.QuizSentences.AnyAsync(sentence => sentence.QuizId == quizId, cancellationToken))
                {
                    return false;
                }

                var quiz = await db.Quizzes.AsNoTracking().SingleOrDefaultAsync(candidate => candidate.Id == quizId && candidate.UserId == userId, cancellationToken);
                if (quiz is null || !Matches(after, "name", quiz.Name) || !MatchesVisibility(after, quiz.IsPublic) || quiz.CollectionId != IdOf(after, "collection_id"))
                {
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                return await quizzes.DeleteQuizAsync(quizId, userId, cancellationToken) is not null;
            }

            case PendingChangeKinds.CreateCollection:
            {
                if (!Guid.TryParse(change.EntityId, out var collectionId)
                    || await db.Quizzes.AnyAsync(quiz => quiz.CollectionId == collectionId, cancellationToken)
                    || await db.Collections.AnyAsync(collection => collection.ParentCollectionId == collectionId, cancellationToken))
                {
                    return false;
                }

                var collection = await OwnedCollectionAsync(change, userId, cancellationToken);
                if (collection is null || !Matches(after, "name", collection.Name) || !MatchesVisibility(after, collection.IsPublic)
                    || collection.ParentCollectionId != IdOf(after, "parent_collection_id"))
                {
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                return await collections.DeleteCollectionAsync(collectionId, userId, cancellationToken);
            }

            case PendingChangeKinds.RenameCollection:
            {
                var collection = await OwnedCollectionAsync(change, userId, cancellationToken);
                var name = Value(before, "name");
                if (collection is null || name is null || !Matches(after, "name", collection.Name))
                {
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                return await collections.RenameCollectionAsync(collection.Id, name, userId, cancellationToken);
            }

            case PendingChangeKinds.MoveQuiz:
            {
                if (!Guid.TryParse(change.EntityId, out var quizId))
                {
                    return false;
                }

                var quiz = await db.Quizzes.AsNoTracking().FirstOrDefaultAsync(candidate => candidate.Id == quizId && candidate.UserId == userId, cancellationToken);
                if (quiz is null || quiz.CollectionId != IdOf(after, "collection_id"))
                {
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                return await collections.MoveQuizToCollectionAsync(quizId, IdOf(before, "collection_id"), userId, cancellationToken);
            }

            case PendingChangeKinds.MoveCollection:
            {
                var collection = await OwnedCollectionAsync(change, userId, cancellationToken);
                if (collection is null || collection.ParentCollectionId != IdOf(after, "parent_collection_id"))
                {
                    return false;
                }

                await db.SaveChangesAsync(cancellationToken);
                return await collections.MoveCollectionAsync(collection.Id, IdOf(before, "parent_collection_id"), userId, cancellationToken);
            }

            default:
                return false;
        }
    }

    private Task<Word?> OwnedWordAsync(AssistantChange change, string userId, CancellationToken cancellationToken) =>
        db.Words.FirstOrDefaultAsync(
            word => word.Id == change.EntityId && word.QuizId == change.QuizId && db.Quizzes.Any(quiz => quiz.Id == word.QuizId && quiz.UserId == userId),
            cancellationToken);

    private Task<QuizSentence?> OwnedSentenceAsync(AssistantChange change, string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(change.EntityId, out var id)
            ? db.QuizSentences.FirstOrDefaultAsync(
                sentence => sentence.Id == id && sentence.QuizId == change.QuizId && db.Quizzes.Any(quiz => quiz.Id == sentence.QuizId && quiz.UserId == userId),
                cancellationToken)
            : Task.FromResult<QuizSentence?>(null);

    private Task<Collection?> OwnedCollectionAsync(AssistantChange change, string userId, CancellationToken cancellationToken) =>
        Guid.TryParse(change.EntityId, out var id)
            ? db.Collections.AsNoTracking().FirstOrDefaultAsync(collection => collection.Id == id && collection.UserId == userId, cancellationToken)
            : Task.FromResult<Collection?>(null);

    private Task<bool> OwnsQuizAsync(Guid quizId, string userId, CancellationToken cancellationToken) =>
        db.Quizzes.AnyAsync(quiz => quiz.Id == quizId && quiz.UserId == userId, cancellationToken);

    private static JsonElement? Read(string? json) =>
        string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<JsonElement>(json);

    private static string? Value(JsonElement? element, string property) =>
        element?.TryGetProperty(property, out var value) == true && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Guid? IdOf(JsonElement? element, string property) =>
        Guid.TryParse(Value(element, property), out var id) ? id : null;

    private static bool MatchesVisibility(JsonElement? element, bool current) =>
        element?.TryGetProperty("is_public", out var value) == true
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
            && value.GetBoolean() == current;

    private static bool Matches(JsonElement? element, string property, string current) =>
        string.Equals(Value(element, property), current, StringComparison.Ordinal);
}
