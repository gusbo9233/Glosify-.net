using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;
using Glosify.Services.CustomQuizzes;

namespace Glosify.Services.Ai.Assistant;

public sealed class ChangeApplier : IChangeApplier
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly ICollectionService _collectionService;
    private readonly ILogger<ChangeApplier> _logger;

    public ChangeApplier(
        GlosifyContext context,
        IQuizService quizService,
        ICollectionService collectionService,
        ILogger<ChangeApplier> logger)
    {
        _context = context;
        _quizService = quizService;
        _collectionService = collectionService;
        _logger = logger;
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

        // Every service used below shares this scoped DbContext. Their intermediate
        // SaveChanges calls therefore participate in this transaction, so a failed
        // later change cannot leave a partially-created quiz or custom quiz behind.
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
        Guid? createdCollectionId = null;
        Guid? createdCustomQuizId = null;
        var customQuizIdsByDraftRef = new Dictionary<string, Guid>(StringComparer.Ordinal);
        var wordIdsByCustomQuizDraftRef = new Dictionary<string, IReadOnlyDictionary<string, string>>(StringComparer.Ordinal);

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
                case PendingChangeKinds.RepairSentence:
                    applied += ApplyRepairSentence(change.Payload, batch);
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
                        createdCustomQuizId ??= created.CustomQuizId;
                        if (created.CustomQuizId.HasValue
                            && change.Payload.TryGetProperty("custom_quiz", out var customQuiz)
                            && !string.IsNullOrWhiteSpace(GetString(customQuiz, "draft_ref")))
                        {
                            var draftRef = GetString(customQuiz, "draft_ref");
                            customQuizIdsByDraftRef[draftRef] = created.CustomQuizId.Value;
                            wordIdsByCustomQuizDraftRef[draftRef] = created.StarterWordIds;
                        }
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
                    applied += await ApplyMoveQuizAsync(change.Payload, userId) ? 1 : 0;
                    break;
                case PendingChangeKinds.RenameCollection:
                    applied += await ApplyRenameCollectionAsync(change.Payload, userId) ? 1 : 0;
                    break;
                case PendingChangeKinds.MoveCollection:
                    applied += await ApplyMoveCollectionAsync(change.Payload, userId) ? 1 : 0;
                    break;
                case PendingChangeKinds.CreateCustomQuiz:
                {
                    var created = await ApplyCreateCustomQuizAsync(change.Payload, userId, cancellationToken);
                    if (created.HasValue)
                    {
                        applied++;
                        createdCustomQuizId ??= created;
                        var draftRef = GetString(change.Payload, "draft_ref");
                        if (!string.IsNullOrWhiteSpace(draftRef))
                        {
                            customQuizIdsByDraftRef[draftRef] = created.Value;
                        }
                    }
                    break;
                }
                case PendingChangeKinds.AddCustomQuizElement:
                    applied += await ApplyAddCustomQuizElementAsync(
                        change.Payload,
                        userId,
                        customQuizIdsByDraftRef,
                        wordIdsByCustomQuizDraftRef,
                        cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.AddCustomQuizElements:
                    applied += await ApplyAddCustomQuizElementsAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.ConfigureCustomQuizElement:
                    applied += await ApplyConfigureCustomQuizElementAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.RemoveCustomQuizElement:
                    applied += await ApplyRemoveCustomQuizElementAsync(change.Payload, userId, cancellationToken) ? 1 : 0;
                    break;
                default:
                    _logger.LogWarning("Unknown pending change kind {Kind}; skipping.", change.Kind);
                    break;
            }
        }

        if (quiz != null && batch.DeletedWordIds.Count > 0)
        {
            await new CustomQuizService(_context).PruneWordBindingsAsync(quiz.Id, batch.DeletedWordIds, cancellationToken);
        }
        await _context.SaveChangesAsync(cancellationToken);
        return new AssistantApplyResult(applied, createdQuizId, createdCollectionId, createdCustomQuizId);
    }

    private static bool RequiresQuizContext(PendingChange change)
    {
        return change.Kind is PendingChangeKinds.AddWord
            or PendingChangeKinds.AddSentence
            or PendingChangeKinds.EditWord
            or PendingChangeKinds.EditSentence
            or PendingChangeKinds.DeleteWord
            or PendingChangeKinds.RepairSentence
            or PendingChangeKinds.DeleteSentence;
    }

    private sealed class QuizContentBatch
    {
        public static readonly QuizContentBatch Empty = new();

        public Dictionary<string, Word> WordsById { get; } = new();
        public HashSet<string> WordLemmas { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<QuizSentence> Sentences { get; } = [];
        public Dictionary<Guid, QuizSentence> SentencesById { get; } = new();
        public HashSet<string> SentenceTexts { get; } = new(StringComparer.OrdinalIgnoreCase);
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

        var needsSentences = changes.Any(change => change.Kind
            is PendingChangeKinds.AddSentence
            or PendingChangeKinds.EditSentence
            or PendingChangeKinds.DeleteSentence
            or PendingChangeKinds.RepairSentence);
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

        return batch;
    }

    private bool ApplyAddWord(JsonElement payload, Quiz quiz, QuizContentBatch batch)
    {
        var newWord = GetString(payload, "word");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(newWord) || string.IsNullOrWhiteSpace(translation))
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
            sentence.Text = newText.Trim();
            batch.SentenceTexts.Add(sentence.Text);
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

    private int ApplyRepairSentence(JsonElement payload, QuizContentBatch batch)
    {
        var original = GetString(payload, "original_text");
        var newText = GetString(payload, "new_text");
        var newTranslation = GetString(payload, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText))
        {
            return 0;
        }

        var matches = batch.Sentences
            .Where(sentence => string.Equals(sentence.Text, original, StringComparison.Ordinal))
            .ToList();

        foreach (var sentence in matches)
        {
            sentence.Text = newText;
            sentence.Translation = newTranslation;
        }
        return matches.Count;
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

        try
        {
            var quiz = await _quizService.CreateQuizAsync(
                name.Trim(),
                sourceLanguage.Trim(),
                targetLanguage.Trim(),
                userId,
                collectionId, cancellationToken: ct);

            var starterWords = AddStarterWords(payload, quiz);
            Guid? customQuizId = null;
            if (payload.TryGetProperty("custom_quiz", out var customQuiz)
                && customQuiz.ValueKind == JsonValueKind.Object)
            {
                await _context.SaveChangesAsync(ct);
                var customName = GetString(customQuiz, "name");
                var document = ParseCustomQuizDocument(customQuiz, starterWords);
                var createdCustom = await new CustomQuizService(_context).CreateAsync(new SaveCustomQuizRequest
                {
                    QuizId = quiz.Id,
                    Name = string.IsNullOrWhiteSpace(customName) ? $"{quiz.Name} custom quiz" : customName,
                    Document = document,
                }, userId, ct);
                customQuizId = createdCustom.Id;
            }
            return new CreatedQuizResult(quiz.Id, customQuizId, starterWords);
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Assistant could not create quiz {QuizName} for user {UserId}", name, userId);
            return null;
        }
    }

    private async Task<Guid?> ApplyCreateCustomQuizAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var quizId = GetNullableGuid(payload, "quiz_id");
        var name = GetString(payload, "name");
        if (!quizId.HasValue || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var created = await new CustomQuizService(_context).CreateAsync(new SaveCustomQuizRequest
            {
                QuizId = quizId.Value,
                Name = name,
                Document = ParseCustomQuizDocument(payload),
            }, userId, ct);
            return created.Id;
        }
        catch (QuizNotFoundException)
        {
            return null;
        }
    }

    private async Task<bool> ApplyAddCustomQuizElementsAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var customQuizId = GetNullableGuid(payload, "custom_quiz_id");
        if (!customQuizId.HasValue)
        {
            return false;
        }

        var service = new CustomQuizService(_context);
        var editor = await service.GetForEditorAsync(customQuizId.Value, userId, ct);
        if (editor == null)
        {
            return false;
        }

        var nextOrder = editor.Document.Blocks.Count == 0 ? 0 : editor.Document.Blocks.Max(block => block.Order) + 1;
        var additions = ParseCustomQuizBlocks(payload);
        foreach (var block in additions)
        {
            block.Order = nextOrder++;
        }
        editor.Document.Blocks.AddRange(additions);
        return await UpdateCustomQuizAsync(service, editor, userId, ct);
    }

    private async Task<bool> ApplyAddCustomQuizElementAsync(
        JsonElement payload,
        string userId,
        IReadOnlyDictionary<string, Guid> customQuizIdsByDraftRef,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> wordIdsByCustomQuizDraftRef,
        CancellationToken ct)
    {
        var customQuizId = GetNullableGuid(payload, "custom_quiz_id");
        var draftRef = GetString(payload, "custom_quiz_ref");
        if (!customQuizId.HasValue
            && (!string.IsNullOrWhiteSpace(draftRef) && customQuizIdsByDraftRef.TryGetValue(draftRef, out var resolved)))
        {
            customQuizId = resolved;
        }
        if (!customQuizId.HasValue
            || !payload.TryGetProperty("block", out var blockElement)
            || blockElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var service = new CustomQuizService(_context);
        var editor = await service.GetForEditorAsync(customQuizId.Value, userId, ct);
        if (editor == null)
        {
            return false;
        }

        IReadOnlyDictionary<string, string>? wordIds = null;
        if (!string.IsNullOrWhiteSpace(draftRef))
        {
            wordIdsByCustomQuizDraftRef.TryGetValue(draftRef, out wordIds);
        }
        var block = ParseCustomQuizBlock(
            blockElement,
            editor.Document.Blocks.Count == 0 ? 0 : editor.Document.Blocks.Max(item => item.Order) + 1,
            wordIds);
        editor.Document.Blocks.Add(block);
        return await UpdateCustomQuizAsync(service, editor, userId, ct);
    }

    private async Task<bool> ApplyConfigureCustomQuizElementAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var customQuizId = GetNullableGuid(payload, "custom_quiz_id");
        var blockId = GetString(payload, "block_id");
        if (!customQuizId.HasValue || string.IsNullOrWhiteSpace(blockId)
            || !payload.TryGetProperty("settings", out var settings)
            || settings.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var service = new CustomQuizService(_context);
        var editor = await service.GetForEditorAsync(customQuizId.Value, userId, ct);
        var block = editor?.Document.Blocks.FirstOrDefault(item => item.Id == blockId);
        if (editor == null || block == null)
        {
            return false;
        }

        ApplyBlockSettings(block, settings);
        return await UpdateCustomQuizAsync(service, editor, userId, ct);
    }

    private async Task<bool> ApplyRemoveCustomQuizElementAsync(JsonElement payload, string userId, CancellationToken ct)
    {
        var customQuizId = GetNullableGuid(payload, "custom_quiz_id");
        var blockId = GetString(payload, "block_id");
        if (!customQuizId.HasValue || string.IsNullOrWhiteSpace(blockId))
        {
            return false;
        }

        var service = new CustomQuizService(_context);
        var editor = await service.GetForEditorAsync(customQuizId.Value, userId, ct);
        if (editor == null || editor.Document.Blocks.RemoveAll(block => block.Id == blockId) == 0)
        {
            return false;
        }
        foreach (var block in editor.Document.Blocks)
        {
            block.TargetInputIds.RemoveAll(id => id == blockId);
        }
        return await UpdateCustomQuizAsync(service, editor, userId, ct);
    }

    private static async Task<bool> UpdateCustomQuizAsync(
        CustomQuizService service,
        CustomQuizEditorDto editor,
        string userId,
        CancellationToken ct)
    {
        var updated = await service.UpdateAsync(editor.Id!.Value, new SaveCustomQuizRequest
        {
            QuizId = editor.QuizId,
            Name = editor.Name,
            Document = editor.Document,
            RowVersion = editor.RowVersion,
        }, userId, ct);
        return updated != null;
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

        try
        {
            var collection = await _collectionService.CreateCollectionAsync(
                name.Trim(),
                language.Trim(),
                userId,
                parentCollectionId, cancellationToken: ct);
            return collection.Id;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Assistant could not create collection {CollectionName} for user {UserId}", name, userId);
            return null;
        }
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

    private Dictionary<string, string> AddStarterWords(JsonElement payload, Quiz quiz)
    {
        var idsByWord = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!payload.TryGetProperty("words", out var wordsElement)
            || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return idsByWord;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in wordsElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var word = GetString(item, "word").Trim();
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
            idsByWord[word] = id;
        }
        return idsByWord;
    }

    private static CustomQuizDocumentV1 ParseCustomQuizDocument(
        JsonElement source,
        IReadOnlyDictionary<string, string>? idsByWord = null) =>
        new()
        {
            SchemaVersion = 1,
            StylePreset = FirstNonBlank(GetString(source, "style_preset"), CustomQuizStylePresets.Editorial),
            Blocks = ParseCustomQuizBlocks(source, idsByWord),
        };

    private static List<CustomQuizBlockV1> ParseCustomQuizBlocks(
        JsonElement source,
        IReadOnlyDictionary<string, string>? idsByWord = null)
    {
        if (!source.TryGetProperty("blocks", out var blocks) || blocks.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var parsed = new List<CustomQuizBlockV1>();
        var order = 0;
        foreach (var item in blocks.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }
            parsed.Add(ParseCustomQuizBlock(item, order++, idsByWord));
        }
        return parsed;
    }

    private static CustomQuizBlockV1 ParseCustomQuizBlock(
        JsonElement item,
        int order,
        IReadOnlyDictionary<string, string>? idsByWord)
    {
        var type = GetString(item, "type");
        var block = new CustomQuizBlockV1
        {
            Id = FirstNonBlank(GetString(item, "id"), Guid.NewGuid().ToString("N")),
            Type = type,
            Order = order,
            ColumnSpan = GetInt(item, "column_span", DefaultSpan(type)),
            GridColumn = GetInt(item, "grid_column", 0),
            GridRow = GetInt(item, "grid_row", 0),
            Text = GetOptionalString(item, "text"),
            Label = GetOptionalString(item, "label"),
            Binding = ParseBinding(item, "binding", idsByWord),
            ExpectedBinding = ParseBinding(item, "expected_binding", idsByWord),
            ExpectedText = GetOptionalString(item, "expected_text"),
            ExpectedChecked = GetBool(item, "expected_checked"),
            TargetInputIds = GetStringArray(item, "target_input_ids"),
        };

        if (item.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            foreach (var option in options.EnumerateArray())
            {
                var binding = ParseBindingValue(option.TryGetProperty("binding", out var value) ? value : default, idsByWord);
                if (binding == null)
                {
                    continue;
                }
                block.Options.Add(new CustomQuizOptionV1
                {
                    Id = FirstNonBlank(GetString(option, "id"), Guid.NewGuid().ToString("N")),
                    Binding = binding,
                    IsCorrect = GetBool(option, "is_correct"),
                });
            }
        }
        return block;
    }

    private static void ApplyBlockSettings(CustomQuizBlockV1 block, JsonElement settings)
    {
        if (HasProperty(settings, "type")) block.Type = GetString(settings, "type");
        if (HasProperty(settings, "column_span")) block.ColumnSpan = GetInt(settings, "column_span", block.ColumnSpan);
        if (HasProperty(settings, "grid_column")) block.GridColumn = GetInt(settings, "grid_column", block.GridColumn);
        if (HasProperty(settings, "grid_row")) block.GridRow = GetInt(settings, "grid_row", block.GridRow);
        if (HasProperty(settings, "text")) block.Text = GetOptionalString(settings, "text");
        if (HasProperty(settings, "label")) block.Label = GetOptionalString(settings, "label");
        if (HasProperty(settings, "binding")) block.Binding = ParseBinding(settings, "binding");
        if (HasProperty(settings, "expected_binding")) block.ExpectedBinding = ParseBinding(settings, "expected_binding");
        if (HasProperty(settings, "expected_text"))
        {
            block.ExpectedText = GetOptionalString(settings, "expected_text")?.Trim();
            if (!string.IsNullOrWhiteSpace(block.ExpectedText) && !HasProperty(settings, "expected_binding"))
            {
                block.ExpectedBinding = null;
            }
        }
        if (HasProperty(settings, "expected_checked")) block.ExpectedChecked = GetBool(settings, "expected_checked");
        if (HasProperty(settings, "target_input_ids")) block.TargetInputIds = GetStringArray(settings, "target_input_ids");
        if (settings.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
        {
            block.Options = [];
            foreach (var option in options.EnumerateArray())
            {
                var binding = ParseBindingValue(option.TryGetProperty("binding", out var value) ? value : default, null);
                if (binding == null) continue;
                block.Options.Add(new CustomQuizOptionV1
                {
                    Id = FirstNonBlank(GetString(option, "id"), Guid.NewGuid().ToString("N")),
                    Binding = binding,
                    IsCorrect = GetBool(option, "is_correct"),
                });
            }
        }
    }

    private static CustomQuizWordBindingV1? ParseBinding(
        JsonElement parent,
        string property,
        IReadOnlyDictionary<string, string>? idsByWord = null)
    {
        if (!parent.TryGetProperty(property, out var binding) || binding.ValueKind == JsonValueKind.Null)
        {
            return null;
        }
        return ParseBindingValue(binding, idsByWord);
    }

    private static CustomQuizWordBindingV1? ParseBindingValue(
        JsonElement binding,
        IReadOnlyDictionary<string, string>? idsByWord)
    {
        if (binding.ValueKind != JsonValueKind.Object)
        {
            return null;
        }
        var wordId = FirstNonBlank(GetString(binding, "word_id"), GetString(binding, "wordId"));
        var word = GetString(binding, "word");
        if (string.IsNullOrWhiteSpace(wordId)
            && !string.IsNullOrWhiteSpace(word)
            && idsByWord != null)
        {
            idsByWord.TryGetValue(word, out wordId);
        }
        return new CustomQuizWordBindingV1
        {
            WordId = wordId ?? string.Empty,
            Field = FirstNonBlank(GetString(binding, "field"), CustomQuizWordFields.Lemma),
        };
    }

    private static int DefaultSpan(string type) => type is CustomQuizBlockTypes.Heading or CustomQuizBlockTypes.InstructionLabel
        ? 12
        : type is CustomQuizBlockTypes.PromptLabel or CustomQuizBlockTypes.TranslationLabel or CustomQuizBlockTypes.SubmitButton
            ? 4
            : 6;

    private static bool HasProperty(JsonElement element, string property) => element.TryGetProperty(property, out _);

    private static int GetInt(JsonElement element, string property, int fallback) =>
        element.TryGetProperty(property, out var value) && value.TryGetInt32(out var parsed) ? parsed : fallback;

    private static bool GetBool(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string? GetOptionalString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static List<string> GetStringArray(JsonElement element, string property) =>
        element.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()!).ToList()
            : [];

    private static string FirstNonBlank(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value;

    private sealed record CreatedQuizResult(
        Guid QuizId,
        Guid? CustomQuizId,
        IReadOnlyDictionary<string, string> StarterWordIds);

}
