using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

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
                    if (created.HasValue)
                    {
                        applied++;
                        createdQuizId ??= created;
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
                default:
                    _logger.LogWarning("Unknown pending change kind {Kind}; skipping.", change.Kind);
                    break;
            }
        }

        await _context.SaveChangesAsync(cancellationToken);
        return new AssistantApplyResult(applied, createdQuizId, createdCollectionId);
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

    private async Task<Guid?> ApplyCreateQuizAsync(JsonElement payload, string userId, CancellationToken ct)
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

            AddStarterWords(payload, quiz);
            return quiz.Id;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Assistant could not create quiz {QuizName} for user {UserId}", name, userId);
            return null;
        }
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

    private void AddStarterWords(JsonElement payload, Quiz quiz)
    {
        if (!payload.TryGetProperty("words", out var wordsElement)
            || wordsElement.ValueKind != JsonValueKind.Array)
        {
            return;
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

            _context.Words.Add(new Word
            {
                Id = Guid.NewGuid().ToString("N"),
                QuizId = quiz.Id,
                Lemma = word,
                Translation = translation,
            });
        }
    }

}
