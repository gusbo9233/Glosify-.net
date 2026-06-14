using System.Text.Json;
using Glosify.Data;
using Glosify.Services.Quizzes;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

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
        if (changes.Any(RequiresQuizContext))
        {
            if (!quizId.HasValue)
            {
                throw new QuizNotFoundException("Choose a quiz before applying quiz content changes.");
            }

            quiz = await _context.Quizzes.FirstOrDefaultAsync(q => q.Id == quizId.Value && q.UserId == userId, cancellationToken)
                ?? throw new QuizNotFoundException();
        }

        var applied = 0;
        Guid? createdQuizId = null;
        Guid? createdCollectionId = null;

        foreach (var change in changes)
        {
            switch (change.Kind)
            {
                case PendingChangeKinds.AddWord:
                    applied += await ApplyAddWordAsync(change.Payload, quiz!, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.AddSentence:
                    applied += await ApplyAddSentenceAsync(change.Payload, quiz!, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.EditWord:
                    applied += await ApplyEditWordAsync(change.Payload, quiz!, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.DeleteWord:
                    applied += await ApplyDeleteWordAsync(change.Payload, quiz!, cancellationToken) ? 1 : 0;
                    break;
                case PendingChangeKinds.RepairSentence:
                    applied += await ApplyRepairSentenceAsync(change.Payload, quiz!, cancellationToken);
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
            or PendingChangeKinds.DeleteWord
            or PendingChangeKinds.RepairSentence;
    }

    private async Task<bool> ApplyAddWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var newWord = GetString(payload, "word", "lemma");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(newWord) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        var exists = await _context.Words
            .AnyAsync(w => w.QuizId == quiz.Id && w.Lemma == newWord, ct);
        if (exists)
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

    private async Task<bool> ApplyAddSentenceAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var text = GetString(payload, "text", "sentence");
        var translation = GetString(payload, "translation");
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
        {
            return false;
        }

        return await AddQuizSentenceAsync(quiz.Id, text, translation, ct);
    }

    private async Task<bool> ApplyEditWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return false;
        }

        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == quiz.Id, ct);
        if (word == null)
        {
            return false;
        }

        var newWord = GetString(payload, "word", "lemma");
        var newTranslation = GetString(payload, "translation");
        if (!string.IsNullOrWhiteSpace(newWord)) word.Lemma = newWord;
        if (!string.IsNullOrWhiteSpace(newTranslation)) word.Translation = newTranslation;
        return true;
    }

    private async Task<bool> ApplyDeleteWordAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var wordId = GetString(payload, "word_id");
        if (string.IsNullOrWhiteSpace(wordId))
        {
            return false;
        }
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId && w.QuizId == quiz.Id, ct);
        if (word == null) return false;
        _context.Words.Remove(word);
        return true;
    }

    private async Task<int> ApplyRepairSentenceAsync(JsonElement payload, Quiz quiz, CancellationToken ct)
    {
        var original = GetString(payload, "original_text");
        var newText = GetString(payload, "new_text");
        var newTranslation = GetString(payload, "new_translation");
        if (string.IsNullOrWhiteSpace(original) || string.IsNullOrWhiteSpace(newText))
        {
            return 0;
        }

        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quiz.Id)
            .ToListAsync(ct);
        var matches = sentences
            .Where(row => string.Equals(
                row.Text,
                original,
                StringComparison.Ordinal))
            .ToList();

        foreach (var sentence in matches)
        {
            sentence.Text = newText;
            sentence.Translation = newTranslation;
        }
        return matches.Count;
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
                collectionId);

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
                parentCollectionId);
            return collection.Id;
        }
        catch (InvalidOperationException ex)
        {
            _logger.LogWarning(ex, "Assistant could not create collection {CollectionName} for user {UserId}", name, userId);
            return null;
        }
    }

    private static string GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var p)) return string.Empty;
        return p.ValueKind == JsonValueKind.String ? p.GetString() ?? string.Empty : string.Empty;
    }

    private static string GetString(JsonElement element, string preferredProperty, string legacyProperty)
    {
        var preferred = GetString(element, preferredProperty);
        return string.IsNullOrWhiteSpace(preferred) ? GetString(element, legacyProperty) : preferred;
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

            var word = GetString(item, "word", "lemma").Trim();
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

    private async Task<bool> AddQuizSentenceAsync(Guid quizId, string text, string translation, CancellationToken ct)
    {
        var cleanText = text.Trim();
        if (string.IsNullOrWhiteSpace(cleanText)
            || _context.QuizSentences.Local.Any(sentence =>
                sentence.QuizId == quizId
                && string.Equals(sentence.Text, cleanText, StringComparison.OrdinalIgnoreCase))
            || await _context.QuizSentences.AnyAsync(sentence =>
                sentence.QuizId == quizId
                && sentence.Text == cleanText,
                ct))
        {
            return false;
        }

        _context.QuizSentences.Add(new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = cleanText,
            Translation = translation.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        });
        return true;
    }
}
