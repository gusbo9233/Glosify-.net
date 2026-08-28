using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Entities;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.Anki;

namespace Glosify.Services.Ai.Assistant;

public sealed class ChangeApplier : IChangeApplier
{
    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly ICollectionService _collectionService;
    private readonly ILogger<ChangeApplier> _logger;
    private readonly IAnkiCollectionService _ankiCollections;

    public ChangeApplier(
        GlosifyContext context,
        IQuizService quizService,
        ICollectionService collectionService,
        ILogger<ChangeApplier> logger,
        IAnkiCollectionService ankiCollections)
    {
        _context = context;
        _quizService = quizService;
        _collectionService = collectionService;
        _logger = logger;
        _ankiCollections = ankiCollections;
    }

    public async Task<AssistantApplyResult> ApplyAsync(
        Guid? quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        if (!_context.Database.IsRelational())
        {
            return await ApplyCoreAsync(quizId, userId, changes, cancellationToken);
        }

        // AssistantChangeWorkflow owns the production transaction so the message status
        // and applied data commit atomically. Direct callers (including focused service
        // tests) still get the all-or-nothing behavior this service has always promised.
        if (_context.Database.CurrentTransaction is not null)
        {
            return await ApplyCoreAsync(quizId, userId, changes, cancellationToken);
        }

        // Every service used below shares this scoped DbContext. Their intermediate
        // SaveChanges calls therefore participate in this transaction, so a failed
        // later change cannot leave a partially-created quiz behind.
        // The execution strategy wrapper is required because Azure SQL retries are
        // enabled for the application's DbContext.
        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var result = await ApplyCoreAsync(quizId, userId, changes, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                // SaveChanges accepts tracked state before a later operation can
                // fail. The database rolls that state back, so detach it before an
                // execution-strategy retry or a caller's recovery SaveChanges.
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<AssistantApplyResult> ApplyCoreAsync(
        Guid? quizId,
        string userId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken cancellationToken)
    {
        if (changes.Any(RetiredPendingChangeKinds.ContainsCustomQuizChange))
        {
            throw new InvalidOperationException("This proposal contains a retired custom quiz change and can no longer be applied.");
        }

        Quiz? quiz = null;
        QuizContentBatch batch = QuizContentBatch.Empty;
        if (changes.Any(RequiresQuizContext))
        {
            if (!quizId.HasValue)
            {
                throw new QuizNotFoundException("Choose a quiz before applying quiz content changes.");
            }

            quiz = await _context.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId.Value && q.UserId == userId, cancellationToken)
                ?? throw new QuizNotFoundException();

            // Bulk applies used to issue one lookup/duplicate-check query per change;
            // pre-loading the touched content keeps this at a fixed handful of queries.
            batch = await LoadQuizContentAsync(quiz.Id, changes, cancellationToken);
        }

        var applied = 0;
        Guid? createdQuizId = null;
        AssistantCreatedQuizSummary? createdQuiz = null;
        Guid? createdCollectionId = null;

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case PendingChangeKinds.AddWord:
                    applied += ApplyAddWord(change.Payload, quiz!, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.AddSentence:
                    applied += ApplyAddSentence(change.Payload, quiz!, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.EditWord:
                    applied += ApplyEditWord(change.Payload, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.EditSentence:
                    applied += ApplyEditSentence(change.Payload, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.DeleteWord:
                    applied += ApplyDeleteWord(change.Payload, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.DeleteSentence:
                    applied += ApplyDeleteSentence(change.Payload, batch) ? 1 : 0;
                    break;
                case PendingChangeKinds.CreateQuiz:
                    {
                        var created = await ApplyCreateQuizAsync(change.Payload, userId, cancellationToken);
                        if (created != null)
                        {
                            applied++;
                            createdQuizId ??= created.QuizId;
                            createdQuiz ??= new AssistantCreatedQuizSummary(
                                created.QuizId,
                                created.Name,
                                created.SourceLanguage,
                                created.TargetLanguage);
                        }
                        break;
                    }
                case PendingChangeKinds.CreateCollection:
                    {
                        var created = await ApplyCreateCollectionAsync(change.Payload, userId, cancellationToken);
                        if (created.HasValue)
                        {
                            applied++;
                            createdCollectionId ??= created;
                        }
                        break;
                    }
                case PendingChangeKinds.MoveQuiz:
                    applied += await ApplyMoveQuizAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.RenameCollection:
                    applied += await ApplyRenameCollectionAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.MoveCollection:
                    applied += await ApplyMoveCollectionAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                default:
                    _logger.LogWarning("Unknown pending change kind {Kind}; skipping.", change.Kind);
                    break;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        if (quiz is not null)
            await _ankiCollections.SyncQuizAsync(quiz.Id, cancellationToken);
        return new AssistantApplyResult(
            applied,
            createdQuizId,
            createdCollectionId,
            createdQuiz);
    }

    private static bool RequiresQuizContext(PendingChange change)
    {
        return change.Kind is PendingChangeKinds.AddWord
            or PendingChangeKinds.AddSentence
            or PendingChangeKinds.EditWord
            or PendingChangeKinds.EditSentence
            or PendingChangeKinds.DeleteWord
            or PendingChangeKinds.DeleteSentence;
    }

    private sealed class QuizContentBatch
    {
        public static readonly QuizContentBatch Empty = new();

        public Dictionary<string, Word> WordsById { get; } = new();
        public HashSet<string> WordLemmas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<QuizSentence> Sentences { get; } = [];
        public Dictionary<Guid, QuizSentence> SentencesById { get; } = new();
        /// <summary>
        /// The text of every sentence the quiz currently holds, counting rows staged by this
        /// proposal. Sentence insertion deduplicates against it.
        /// </summary>
        /// <remarks>
        /// Only meaningful if it tracks the current state: a text left behind after its row was
        /// deleted or rewritten blocks a legitimate re-add, and a text dropped while a staged
        /// row still carries it admits a duplicate. Every mutation goes through
        /// <see cref="ReleaseSentenceText"/> or adds here, never one without the other.
        /// </remarks>
        public HashSet<string> SentenceTexts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Text of sentences this proposal has staged for insert. They are not in
        /// <see cref="Sentences"/>, which holds only rows loaded from the database.
        /// </summary>
        public HashSet<string> StagedSentenceTexts { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// Normalized text of every sentence this quiz will hold once the proposal is applied:
        /// the ones already stored plus the ones being added.
        /// </summary>
        /// <remarks>
        /// Separate from <see cref="SentenceTexts"/>, which is the exact-text set that sentence
        /// insertion dedupes against and must keep its existing semantics. This one exists to
        /// stop the same text being filed as vocabulary as well, and is built from the whole
        /// proposal so the answer does not depend on the order the model made its calls in.
        /// </remarks>
        public HashSet<string> SentenceMatchKeys { get; } = new(StringComparer.OrdinalIgnoreCase);
        public HashSet<string> DeletedWordIds { get; } = new(StringComparer.Ordinal);
    }

    private async Task<QuizContentBatch> LoadQuizContentAsync(
        Guid quizId,
        IReadOnlyList<PendingChange> changes,
        CancellationToken ct)
    {
        var batch = new QuizContentBatch();

        var wordIds = changes
            .Where(change => change.Kind is PendingChangeKinds.EditWord or PendingChangeKinds.DeleteWord)
            .Select(change => GetString(change.Payload, "word_id"))
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct()
            .ToList();
        if (wordIds.Count > 0)
        {
            var words = await _context.Words
                .Where(word => word.QuizId == quizId && wordIds.Contains(word.Id))
                .ToListAsync(ct);
            foreach (var word in words)
            {
                batch.WordsById[word.Id] = word;
            }
        }

        if (changes.Any(change => change.Kind == PendingChangeKinds.AddWord))
        {
            var lemmas = await _context.Words
                .Where(word => word.QuizId == quizId)
                .Select(word => word.Lemma)
                .ToListAsync(ct);
            batch.WordLemmas.UnionWith(lemmas);
        }

        // AddWord is in this list because a word may not repeat a sentence the quiz already
        // holds, which cannot be known without loading them.
        var needsSentences = changes.Any(change => change.Kind
            is PendingChangeKinds.AddSentence
            or PendingChangeKinds.EditSentence
            or PendingChangeKinds.DeleteSentence
            or PendingChangeKinds.AddWord);
        if (needsSentences)
        {
            var sentences = await _context.QuizSentences
                .Where(sentence => sentence.QuizId == quizId)
                .ToListAsync(ct);
            batch.Sentences.AddRange(sentences);
            foreach (var sentence in sentences)
            {
                batch.SentencesById[sentence.Id] = sentence;
                batch.SentenceTexts.Add(sentence.Text);
            }
        }

        ProjectSentenceMatchKeys(batch, changes);

        return batch;
    }

    /// <summary>
    /// Fills <see cref="QuizContentBatch.SentenceMatchKeys"/> with the sentences the quiz will
    /// hold once this proposal has been applied.
    /// </summary>
    /// <remarks>
    /// A word is judged against the outcome rather than the starting point. Otherwise the two
    /// interesting cases both go wrong: a sentence the proposal deletes would keep blocking a
    /// word that should replace it — "delete that sentence and add it as vocabulary instead"
    /// is an ordinary request — and a sentence the proposal introduces by editing would not
    /// block one, letting the same text land in both tables.
    /// <para>
    /// Changes are walked in order so a deletion followed by an edit behaves the way the apply
    /// loop behaves: the edit finds nothing and does nothing.
    /// </para>
    /// </remarks>
    private static void ProjectSentenceMatchKeys(
        QuizContentBatch batch,
        IReadOnlyList<PendingChange> changes)
    {
        var projected = batch.Sentences.ToDictionary(
            sentence => sentence.Id,
            sentence => sentence.Text);
        var added = new List<string>();

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case PendingChangeKinds.AddSentence:
                {
                    // Only a sentence that will actually be inserted may displace a word; one
                    // missing its translation is skipped, and the content would vanish.
                    var text = GetString(change.Payload, "text").Trim();
                    var translation = GetString(change.Payload, "translation").Trim();
                    if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(translation))
                    {
                        added.Add(text);
                    }
                    break;
                }

                case PendingChangeKinds.EditSentence:
                {
                    var id = GetNullableGuid(change.Payload, "sentence_id");
                    var text = GetString(change.Payload, "text").Trim();
                    if (id.HasValue && !string.IsNullOrWhiteSpace(text) && projected.ContainsKey(id.Value))
                    {
                        projected[id.Value] = text;
                    }
                    break;
                }

                case PendingChangeKinds.DeleteSentence:
                {
                    var id = GetNullableGuid(change.Payload, "sentence_id");
                    if (id.HasValue)
                    {
                        projected.Remove(id.Value);
                    }
                    break;
                }

            }
        }

        foreach (var text in projected.Values.Concat(added))
        {
            var key = Tools.ToolArguments.NormalizeForDuplicateMatch(text);
            if (!string.IsNullOrWhiteSpace(key))
            {
                batch.SentenceMatchKeys.Add(key);
            }
        }
    }

    private bool ApplyAddWord(JsonElement payload, Quiz quiz, QuizContentBatch batch)
    {
        var newWord = GetString(payload, "word");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(newWord) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        if (batch.SentenceMatchKeys.Contains(Tools.ToolArguments.NormalizeForDuplicateMatch(newWord)))
        {
            return false;
        }

        if (!batch.WordLemmas.Add(newWord))
        {
            return false;
        }

        _context.Words.Add(new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quiz.Id,
            Lemma = newWord,
            Translation = translation,
        });

        return true;
    }

    private bool ApplyAddSentence(JsonElement payload, Quiz quiz, QuizContentBatch batch)
    {
        var text = GetString(payload, "text").Trim();
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        if (!batch.SentenceTexts.Add(text))
        {
            return false;
        }

        // Staged rows are not in batch.Sentences, so a later delete or edit has to be told
        // about them or it would release a text this insert still needs.
        batch.StagedSentenceTexts.Add(text);
        _context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quiz.Id,
            Text = text,
            Translation = translation.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        return true;
    }

    private bool ApplyEditWord(JsonElement payload, QuizContentBatch batch)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId) || !batch.WordsById.TryGetValue(wordId, out var word))
        {
            return false;
        }

        var newWord = GetString(payload, "word");
        var newTranslation = GetString(payload, "translation");
        if (!string.IsNullOrWhiteSpace(newWord))
        {
            word.Lemma = newWord;
            batch.WordLemmas.Add(newWord);
        }
        if (!string.IsNullOrWhiteSpace(newTranslation)) word.Translation = newTranslation;
        return true;
    }

    private bool ApplyEditSentence(JsonElement payload, QuizContentBatch batch)
    {
        var sentenceId = GetNullableGuid(payload, "sentence_id");
        if (!sentenceId.HasValue || !batch.SentencesById.TryGetValue(sentenceId.Value, out var sentence))
        {
            return false;
        }

        var newText = GetString(payload, "text");
        var newTranslation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(newText) && string.IsNullOrWhiteSpace(newTranslation))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(newText))
        {
            var replaced = sentence.Text;
            sentence.Text = newText.Trim();
            batch.SentenceTexts.Add(sentence.Text);
            // The text this row used to hold is only still spoken for if something else holds
            // it; otherwise it stays in the set and blocks a legitimate re-add of it later.
            ReleaseSentenceText(batch, replaced);
        }
        if (!string.IsNullOrWhiteSpace(newTranslation))
        {
            sentence.Translation = newTranslation.Trim();
        }
        return true;
    }

    private bool ApplyDeleteWord(JsonElement payload, QuizContentBatch batch)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId) || !batch.WordsById.TryGetValue(wordId, out var word))
        {
            return false;
        }

        batch.WordsById.Remove(wordId);
        batch.DeletedWordIds.Add(wordId);
        _context.Words.Remove(word);
        return true;
    }

    /// <summary>
    /// Drops a text from the dedupe set once nothing in the quiz carries it any more.
    /// </summary>
    /// <remarks>
    /// Checks staged inserts as well as loaded rows: a sentence added earlier in this proposal
    /// is not in <see cref="QuizContentBatch.Sentences"/>, and forgetting it would let the same
    /// text be inserted twice.
    /// </remarks>
    private static void ReleaseSentenceText(QuizContentBatch batch, string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var stillUsed = batch.Sentences.Any(remaining =>
            string.Equals(remaining.Text, text, StringComparison.OrdinalIgnoreCase))
            || batch.StagedSentenceTexts.Contains(text);
        if (!stillUsed)
        {
            batch.SentenceTexts.Remove(text);
        }
    }

    private bool ApplyDeleteSentence(JsonElement payload, QuizContentBatch batch)
    {
        var sentenceId = GetNullableGuid(payload, "sentence_id");
        if (!sentenceId.HasValue || !batch.SentencesById.TryGetValue(sentenceId.Value, out var sentence))
        {
            return false;
        }

        batch.SentencesById.Remove(sentenceId.Value);
        batch.Sentences.Remove(sentence);
        // The text has to leave the dedupe set as well, or re-adding it later in the same
        // proposal is refused as a duplicate of the row this just deleted — which is how
        // "delete that sentence and add it back with a better translation" lost the sentence
        // altogether.
        ReleaseSentenceText(batch, sentence.Text);
        _context.QuizSentences.Remove(sentence);
        return true;
    }

    private async Task<CreatedQuizResult?> ApplyCreateQuizAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var name = GetString(payload, "name");
        var sourceLanguage = GetString(payload, "source_language");
        var targetLanguage = GetString(payload, "target_language");
        var collectionId = GetNullableGuid(payload, "collection_id");

        if (string.IsNullOrWhiteSpace(name)
            || string.IsNullOrWhiteSpace(sourceLanguage)
            || string.IsNullOrWhiteSpace(targetLanguage))
        {
            return null;
        }

        var quiz = await _quizService.CreateQuizAsync(
            name.Trim(),
            sourceLanguage.Trim(),
            targetLanguage.Trim(),
            userId,
            collectionId, cancellationToken: ct);

        AddStarterWords(payload, quiz);
        AddStarterSentences(payload, quiz);
        return new CreatedQuizResult(
            quiz.Id,
            quiz.Name,
            quiz.SourceLanguage,
            quiz.TargetLanguage);
    }

    private async Task<Guid?> ApplyCreateCollectionAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var name = GetString(payload, "name");
        var language = GetString(payload, "language");
        var parentCollectionId = GetNullableGuid(payload, "parent_collection_id");

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(language))
        {
            return null;
        }

        var collection = await _collectionService.CreateCollectionAsync(
            name.Trim(),
            language.Trim(),
            userId,
            parentCollectionId, cancellationToken: ct);
        return collection.Id;
    }

    private async Task<bool> ApplyMoveQuizAsync(JsonElement payload, string userId, CancellationToken cancellationToken = default)
    {
        var quizId = GetNullableGuid(payload, "quiz_id");
        if (!quizId.HasValue)
        {
            return false;
        }

        var collectionId = GetNullableGuid(payload, "collection_id");
        return await _collectionService.MoveQuizToCollectionAsync(quizId.Value, collectionId, userId, cancellationToken: cancellationToken);
    }

    private async Task<bool> ApplyRenameCollectionAsync(JsonElement payload, string userId, CancellationToken cancellationToken = default)
    {
        var collectionId = GetNullableGuid(payload, "collection_id");
        var name = GetString(payload, "name");
        if (!collectionId.HasValue || string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        return await _collectionService.RenameCollectionAsync(collectionId.Value, name.Trim(), userId, cancellationToken: cancellationToken);
    }

    private async Task<bool> ApplyMoveCollectionAsync(JsonElement payload, string userId, CancellationToken cancellationToken = default)
    {
        var collectionId = GetNullableGuid(payload, "collection_id");
        if (!collectionId.HasValue)
        {
            return false;
        }

        var parentCollectionId = GetNullableGuid(payload, "parent_collection_id");
        return await _collectionService.MoveCollectionAsync(collectionId.Value, parentCollectionId, userId, cancellationToken: cancellationToken);
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }

    private static Guid? GetNullableGuid(JsonElement element, string property)
    {
        var value = GetString(element, property);
        return Guid.TryParse(value, out var parsed) ? parsed : null;
    }

    private void AddStarterWords(JsonElement payload, Quiz quiz)
    {
        if (!payload.TryGetProperty("words", out var wordsElement)
            || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        // A payload can be applied long after it was proposed, so the same text arriving in
        // both collections is filtered here too rather than trusted to have been caught when
        // the proposal was built. Storing it twice is never what was asked for.
        var sentenceTexts = StarterSentenceTexts(payload);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in wordsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var word = GetString(item, "word").Trim();
            if (sentenceTexts.Contains(Tools.ToolArguments.NormalizeForDuplicateMatch(word)))
            {
                continue;
            }
            var translation = GetString(item, "translation").Trim();
            if (string.IsNullOrWhiteSpace(word)
                || string.IsNullOrWhiteSpace(translation)
                || !seen.Add(word))
            {
                continue;
            }

            var id = Guid.NewGuid().ToString("N");
            _context.Words.Add(new Word
            {
                Id = id,
                QuizId = quiz.Id,
                Lemma = word,
                Translation = translation,
            });
        }
    }

    /// <summary>The trimmed text of every valid starter sentence in a create-quiz payload.</summary>
    private static HashSet<string> StarterSentenceTexts(JsonElement payload)
    {
        var texts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!payload.TryGetProperty("sentences", out var sentencesElement)
            || sentencesElement.ValueKind != JsonValueKind.Array)
        {
            return texts;
        }

        foreach (var item in sentencesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            // Only sentences that will actually be stored may displace a word. A sentence
            // missing its translation is skipped below, so counting it here would drop the
            // matching word and store neither: the content would vanish entirely.
            var text = Tools.ToolArguments.NormalizeForDuplicateMatch(GetString(item, "text"));
            var translation = GetString(item, "translation").Trim();
            if (!string.IsNullOrWhiteSpace(text) && !string.IsNullOrWhiteSpace(translation))
            {
                texts.Add(text);
            }
        }

        return texts;
    }

    /// <summary>
    /// Adds the sentences a create-quiz proposal carried, alongside its starter words.
    /// </summary>
    /// <remarks>
    /// Sentences are a separate collection from words on purpose: a full sentence stored as
    /// vocabulary is the bug this exists to prevent. Like <see cref="AddStarterWords"/> this
    /// only stages the rows — the caller's transaction owns when they become durable.
    /// </remarks>
    private void AddStarterSentences(JsonElement payload, Quiz quiz)
    {
        if (!payload.TryGetProperty("sentences", out var sentencesElement)
            || sentencesElement.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sentencesElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var text = GetString(item, "text").Trim();
            var translation = GetString(item, "translation").Trim();
            if (string.IsNullOrWhiteSpace(text)
                || string.IsNullOrWhiteSpace(translation)
                || !seen.Add(text))
            {
                continue;
            }

            _context.QuizSentences.Add(new QuizSentence
            {
                Id = Guid.NewGuid(),
                QuizId = quiz.Id,
                Text = text,
                Translation = translation,
                CreatedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    private sealed record CreatedQuizResult(
        Guid QuizId,
        string Name,
        string SourceLanguage,
        string TargetLanguage);

}
