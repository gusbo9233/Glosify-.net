using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Glosify.Data;
using Glosify.Infrastructure.Concurrency;
using Glosify.Models.Entities;
using Glosify.Models.QuizImports;
using Glosify.Services.Language;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Quizzes;

public sealed class QuizJsonImportService : IQuizJsonImportService
{
    public const int MaxJsonBytes = 64 * 1024;
    public const int MaxCollections = 25;
    public const int MaxQuizzes = 50;
    public const int MaxItemsPerQuiz = 100;
    public const int MaxTotalItems = 1_000;
    public const int MaxCollectionDepth = 5;

    private static readonly JsonSerializerOptions ReadOptions = new(JsonSerializerDefaults.Web)
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 64,
    };

    private static readonly JsonSerializerOptions WriteOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly GlosifyContext _context;
    private readonly IKeyedAsyncLock _keyedLock;
    private readonly TimeProvider _timeProvider;

    public QuizJsonImportService(
        GlosifyContext context,
        IKeyedAsyncLock keyedLock,
        TimeProvider timeProvider)
    {
        _context = context;
        _keyedLock = keyedLock;
        _timeProvider = timeProvider;
    }

    public async Task<QuizJsonImportPreview> PreviewAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        var validated = await ValidateAsync(
            json,
            targetLanguage,
            parentCollectionId,
            userId,
            cancellationToken);
        return validated.Preview;
    }

    public async Task<QuizJsonImportResult> ApplyAsync(
        string json,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken = default)
    {
        await using var importLock = await _keyedLock.AcquireAsync(
            $"quiz-json-import:{userId}",
            cancellationToken);

        if (!_context.Database.IsRelational())
        {
            var validated = await ValidateAsync(
                json,
                targetLanguage,
                parentCollectionId,
                userId,
                cancellationToken);
            return await PersistAsync(validated, userId, cancellationToken);
        }

        var strategy = _context.Database.CreateExecutionStrategy();
        return await strategy.ExecuteAsync(async () =>
        {
            await using var transaction = await _context.Database.BeginTransactionAsync(cancellationToken);
            try
            {
                var validated = await ValidateAsync(
                    json,
                    targetLanguage,
                    parentCollectionId,
                    userId,
                    cancellationToken);
                var result = await PersistAsync(validated, userId, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return result;
            }
            catch
            {
                _context.ChangeTracker.Clear();
                throw;
            }
        });
    }

    private async Task<ValidatedImport> ValidateAsync(
        string rawJson,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        CancellationToken cancellationToken)
    {
        var errors = new ValidationErrors();
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            errors.Add("$.json", "Paste a JSON import document.");
            throw errors.ToException();
        }

        if (Encoding.UTF8.GetByteCount(rawJson) > MaxJsonBytes)
        {
            errors.Add("$.json", $"The JSON document must be {MaxJsonBytes / 1024} KiB or smaller.");
            throw errors.ToException();
        }

        var (candidate, wrapperRepaired) = ExtractCandidate(rawJson);
        var tolerantRepair = !CanParseStrictly(candidate);
        QuizJsonImportDocumentV1? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<QuizJsonImportDocumentV1>(candidate, ReadOptions);
        }
        catch (JsonException exception)
        {
            errors.Add(
                string.IsNullOrWhiteSpace(exception.Path) ? "$" : exception.Path,
                "The text is not valid Glosify import JSON. Free repair only handles wrappers, comments, and trailing commas.");
            throw errors.ToException();
        }

        if (parsed is null)
        {
            errors.Add("$", "The JSON import document must be an object.");
            throw errors.ToException();
        }

        var warnings = new List<string>();
        var counters = new ImportCounters();
        var normalized = NormalizeDocument(parsed, targetLanguage, errors, warnings, counters);
        var provisionalJson = JsonSerializer.Serialize(normalized, WriteOptions);

        if (counters.CollectionCount > MaxCollections)
        {
            errors.Add("$.collections", $"An import may contain at most {MaxCollections} collections.");
        }
        if (counters.QuizCount > MaxQuizzes)
        {
            errors.Add("$.quizzes", $"An import may contain at most {MaxQuizzes} quizzes.");
        }
        if (counters.RawItemCount > MaxTotalItems)
        {
            errors.Add("$", $"An import may contain at most {MaxTotalItems} word and sentence items.");
        }

        var canonicalTarget = targetLanguage?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(canonicalTarget) || canonicalTarget.Length > 64)
        {
            errors.Add("$.target_language", "The selected quiz language is invalid.");
        }

        await ValidateDestinationAsync(
            normalized,
            canonicalTarget,
            parentCollectionId,
            userId,
            errors,
            cancellationToken);

        if (errors.Count > 0)
        {
            throw errors.ToException(provisionalJson);
        }

        var totals = new QuizJsonImportTotals(
            counters.CollectionCount,
            counters.QuizCount,
            counters.WordCount,
            counters.SentenceCount);
        var preview = new QuizJsonImportPreview(
            provisionalJson,
            wrapperRepaired || tolerantRepair,
            canonicalTarget,
            parentCollectionId,
            totals,
            normalized.Quizzes!.Select(quiz => ToPreview(quiz, normalized.SourceLanguage!, canonicalTarget)).ToList(),
            normalized.Collections!.Select(collection => ToPreview(collection, normalized.SourceLanguage!, canonicalTarget)).ToList(),
            warnings);
        return new ValidatedImport(normalized, preview);
    }

    private async Task ValidateDestinationAsync(
        QuizJsonImportDocumentV1 document,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        ValidationErrors errors,
        CancellationToken cancellationToken)
    {
        if (parentCollectionId.HasValue)
        {
            var parentIsValid = await _context.Collections.AsNoTracking().AnyAsync(collection =>
                collection.Id == parentCollectionId.Value
                && collection.UserId == userId
                && collection.Language == targetLanguage,
                cancellationToken);
            if (!parentIsValid)
            {
                errors.Add("$.parent_collection_id", "The destination collection was not found in the selected language.");
                return;
            }
        }

        var topLevelNames = document.Collections!
            .Select(collection => collection.Name!)
            .ToList();
        if (topLevelNames.Count == 0)
        {
            return;
        }

        var existingNames = await _context.Collections.AsNoTracking()
            .Where(collection =>
                collection.UserId == userId
                && collection.Language == targetLanguage
                && collection.ParentCollectionId == parentCollectionId)
            .Select(collection => collection.Name)
            .ToListAsync(cancellationToken);
        if (topLevelNames.Any(name => existingNames.Contains(name, StringComparer.OrdinalIgnoreCase)))
        {
            throw new CollectionNameConflictException();
        }
    }

    private static QuizJsonImportDocumentV1 NormalizeDocument(
        QuizJsonImportDocumentV1 document,
        string targetLanguage,
        ValidationErrors errors,
        List<string> warnings,
        ImportCounters counters)
    {
        if (document.Version != 1)
        {
            errors.Add("$.version", "Only Glosify JSON import version 1 is supported.");
        }

        var isFreestyle = QuizLanguageCatalog.IsFreestyle(targetLanguage);
        var sourceLanguage = isFreestyle
            ? QuizLanguageCatalog.FreestyleName
            : RequiredTrimmed(
                document.SourceLanguage,
                "$.source_language",
                64,
                errors,
                "Source language is required.");
        var quizzes = NormalizeQuizList(
            document.Quizzes,
            "$.quizzes",
            sourceLanguage,
            targetLanguage,
            errors,
            warnings,
            counters);
        var collections = NormalizeCollectionList(
            document.Collections,
            "$.collections",
            1,
            sourceLanguage,
            targetLanguage,
            errors,
            warnings,
            counters);

        if (quizzes.Count == 0 && collections.Count == 0)
        {
            errors.Add("$", "Include at least one quiz or collection.");
        }

        return new QuizJsonImportDocumentV1
        {
            Version = 1,
            SourceLanguage = sourceLanguage,
            Quizzes = quizzes,
            Collections = collections,
        };
    }

    private static List<QuizJsonImportCollectionV1> NormalizeCollectionList(
        List<QuizJsonImportCollectionV1>? collections,
        string path,
        int depth,
        string defaultSourceLanguage,
        string targetLanguage,
        ValidationErrors errors,
        List<string> warnings,
        ImportCounters counters)
    {
        if (collections is null)
        {
            errors.Add(path, "Collections must be an array.");
            return [];
        }

        var normalized = new List<QuizJsonImportCollectionV1>();
        var siblingNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < collections.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var collection = collections[index];
            if (collection is null)
            {
                errors.Add(itemPath, "A collection must be an object.");
                continue;
            }

            counters.CollectionCount++;
            var name = RequiredTrimmed(collection.Name, $"{itemPath}.name", 120, errors, "Collection name is required.");
            if (!string.IsNullOrWhiteSpace(name) && !siblingNames.Add(name))
            {
                throw new CollectionNameConflictException();
            }

            if (depth > MaxCollectionDepth)
            {
                errors.Add(itemPath, $"Collections may be nested at most {MaxCollectionDepth} levels deep.");
                continue;
            }

            normalized.Add(new QuizJsonImportCollectionV1
            {
                Name = name,
                Quizzes = NormalizeQuizList(
                    collection.Quizzes,
                    $"{itemPath}.quizzes",
                    defaultSourceLanguage,
                    targetLanguage,
                    errors,
                    warnings,
                    counters),
                Collections = NormalizeCollectionList(
                    collection.Collections,
                    $"{itemPath}.collections",
                    depth + 1,
                    defaultSourceLanguage,
                    targetLanguage,
                    errors,
                    warnings,
                    counters),
            });
        }

        return normalized;
    }

    private static List<QuizJsonImportQuizV1> NormalizeQuizList(
        List<QuizJsonImportQuizV1>? quizzes,
        string path,
        string defaultSourceLanguage,
        string targetLanguage,
        ValidationErrors errors,
        List<string> warnings,
        ImportCounters counters)
    {
        if (quizzes is null)
        {
            errors.Add(path, "Quizzes must be an array.");
            return [];
        }

        var normalized = new List<QuizJsonImportQuizV1>();
        for (var index = 0; index < quizzes.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var quiz = quizzes[index];
            if (quiz is null)
            {
                errors.Add(itemPath, "A quiz must be an object.");
                continue;
            }

            counters.QuizCount++;
            var name = RequiredTrimmed(quiz.Name, $"{itemPath}.name", 120, errors, "Quiz name is required.");
            var isFreestyle = QuizLanguageCatalog.IsFreestyle(targetLanguage);
            var sourceLanguage = isFreestyle
                ? QuizLanguageCatalog.FreestyleName
                : string.IsNullOrWhiteSpace(quiz.SourceLanguage)
                ? defaultSourceLanguage
                : RequiredTrimmed(
                    quiz.SourceLanguage,
                    $"{itemPath}.source_language",
                    64,
                    errors,
                    "Source language is required.");
            var rawWordCount = quiz.Words?.Count ?? 0;
            var rawSentenceCount = quiz.Sentences?.Count ?? 0;
            counters.RawItemCount += rawWordCount + rawSentenceCount;
            if (rawWordCount + rawSentenceCount > MaxItemsPerQuiz)
            {
                errors.Add(itemPath, $"A quiz may contain at most {MaxItemsPerQuiz} words and sentences.");
            }

            if (isFreestyle && rawSentenceCount > 0)
            {
                errors.Add($"{itemPath}.sentences", "Sentence items are not supported in Freestyle mode. Use prompt and answer items instead.");
            }
            var sentences = isFreestyle
                ? []
                : NormalizeSentences(quiz.Sentences, $"{itemPath}.sentences", errors, warnings);
            var sentenceTexts = sentences.Select(sentence => sentence.Text!).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var words = NormalizeWords(quiz.Words, $"{itemPath}.words", sentenceTexts, errors, warnings);
            if (words.Count == 0 && sentences.Count == 0)
            {
                errors.Add(itemPath, "A quiz must contain at least one valid word or sentence.");
            }

            counters.WordCount += words.Count;
            counters.SentenceCount += sentences.Count;
            normalized.Add(new QuizJsonImportQuizV1
            {
                Name = name,
                SourceLanguage = string.Equals(sourceLanguage, defaultSourceLanguage, StringComparison.Ordinal)
                    ? null
                    : sourceLanguage,
                Words = words,
                Sentences = sentences,
            });
        }

        return normalized;
    }

    private static List<QuizJsonImportWordV1> NormalizeWords(
        List<QuizJsonImportWordV1>? words,
        string path,
        IReadOnlySet<string> sentenceTexts,
        ValidationErrors errors,
        List<string> warnings)
    {
        if (words is null)
        {
            errors.Add(path, "Words must be an array.");
            return [];
        }

        var normalized = new List<QuizJsonImportWordV1>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < words.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var item = words[index];
            if (item is null)
            {
                errors.Add(itemPath, "A word must be an object.");
                continue;
            }

            var word = RequiredTrimmed(item.Word, $"{itemPath}.word", 200, errors, "Word is required.");
            var translation = RequiredTrimmed(item.Translation, $"{itemPath}.translation", 500, errors, "Translation is required.");
            if (string.IsNullOrWhiteSpace(word) || string.IsNullOrWhiteSpace(translation))
            {
                continue;
            }
            if (!seen.Add(word))
            {
                warnings.Add($"Removed duplicate word at {itemPath}; the first '{word}' was kept.");
                continue;
            }
            if (sentenceTexts.Contains(word))
            {
                warnings.Add($"Removed word at {itemPath} because the same text is imported as a sentence.");
                continue;
            }

            normalized.Add(new QuizJsonImportWordV1 { Word = word, Translation = translation });
        }

        return normalized;
    }

    private static List<QuizJsonImportSentenceV1> NormalizeSentences(
        List<QuizJsonImportSentenceV1>? sentences,
        string path,
        ValidationErrors errors,
        List<string> warnings)
    {
        if (sentences is null)
        {
            errors.Add(path, "Sentences must be an array.");
            return [];
        }

        var normalized = new List<QuizJsonImportSentenceV1>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < sentences.Count; index++)
        {
            var itemPath = $"{path}[{index}]";
            var item = sentences[index];
            if (item is null)
            {
                errors.Add(itemPath, "A sentence must be an object.");
                continue;
            }

            var text = RequiredTrimmed(item.Text, $"{itemPath}.text", 4_000, errors, "Sentence text is required.");
            var translation = RequiredTrimmed(item.Translation, $"{itemPath}.translation", 4_000, errors, "Translation is required.");
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(translation))
            {
                continue;
            }
            if (!seen.Add(text))
            {
                warnings.Add($"Removed duplicate sentence at {itemPath}; the first sentence was kept.");
                continue;
            }

            normalized.Add(new QuizJsonImportSentenceV1 { Text = text, Translation = translation });
        }

        return normalized;
    }

    private async Task<QuizJsonImportResult> PersistAsync(
        ValidatedImport validated,
        string userId,
        CancellationToken cancellationToken)
    {
        var document = validated.Document;
        var targetLanguage = validated.Preview.TargetLanguage;
        var now = _timeProvider.GetUtcNow();

        foreach (var quiz in document.Quizzes!)
        {
            AddQuiz(quiz, document.SourceLanguage!, targetLanguage, validated.Preview.ParentCollectionId, userId, now);
        }
        foreach (var collection in document.Collections!)
        {
            AddCollection(
                collection,
                document.SourceLanguage!,
                targetLanguage,
                validated.Preview.ParentCollectionId,
                userId,
                now);
        }

        await _context.SaveChangesAsync(cancellationToken);
        var totals = validated.Preview.Totals;
        return new QuizJsonImportResult(
            totals.CollectionCount,
            totals.QuizCount,
            totals.WordCount,
            totals.SentenceCount);
    }

    private void AddCollection(
        QuizJsonImportCollectionV1 source,
        string defaultSourceLanguage,
        string targetLanguage,
        Guid? parentCollectionId,
        string userId,
        DateTimeOffset now)
    {
        var collection = new Collection
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Name = source.Name!,
            Language = targetLanguage,
            ParentCollectionId = parentCollectionId,
            CreatedAt = now,
            IsPublic = false,
        };
        _context.Collections.Add(collection);

        foreach (var quiz in source.Quizzes!)
        {
            AddQuiz(quiz, defaultSourceLanguage, targetLanguage, collection.Id, userId, now);
        }
        foreach (var child in source.Collections!)
        {
            AddCollection(child, defaultSourceLanguage, targetLanguage, collection.Id, userId, now);
        }
    }

    private void AddQuiz(
        QuizJsonImportQuizV1 source,
        string defaultSourceLanguage,
        string targetLanguage,
        Guid? collectionId,
        string userId,
        DateTimeOffset now)
    {
        var quizId = Guid.NewGuid();
        _context.Quizzes.Add(new Quiz
        {
            Id = quizId,
            Name = source.Name!,
            UserId = userId,
            CollectionId = collectionId,
            CreatedAt = now,
            ProcessingStatus = "Ready",
            SourceLanguage = source.SourceLanguage ?? defaultSourceLanguage,
            TargetLanguage = targetLanguage,
            Language = targetLanguage,
            IsPublic = false,
        });
        _context.Words.AddRange(source.Words!.Select(word => new Word
        {
            Id = Guid.NewGuid().ToString("N"),
            QuizId = quizId,
            Lemma = word.Word!,
            Translation = word.Translation!,
            CreatedAt = now,
        }));
        _context.QuizSentences.AddRange(source.Sentences!.Select(sentence => new QuizSentence
        {
            Id = Guid.NewGuid(),
            QuizId = quizId,
            Text = sentence.Text!,
            Translation = sentence.Translation!,
            CreatedAt = now,
        }));
    }

    private static QuizJsonImportQuizPreview ToPreview(
        QuizJsonImportQuizV1 quiz,
        string defaultSourceLanguage,
        string targetLanguage) =>
        new(
            quiz.Name!,
            quiz.SourceLanguage ?? defaultSourceLanguage,
            targetLanguage,
            quiz.Words!.Count,
            quiz.Sentences!.Count);

    private static QuizJsonImportCollectionPreview ToPreview(
        QuizJsonImportCollectionV1 collection,
        string defaultSourceLanguage,
        string targetLanguage) =>
        new(
            collection.Name!,
            collection.Quizzes!.Select(quiz => ToPreview(quiz, defaultSourceLanguage, targetLanguage)).ToList(),
            collection.Collections!.Select(child => ToPreview(child, defaultSourceLanguage, targetLanguage)).ToList());

    private static string RequiredTrimmed(
        string? value,
        string path,
        int maximumLength,
        ValidationErrors errors,
        string requiredMessage)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
        {
            errors.Add(path, requiredMessage);
        }
        else if (normalized.Length > maximumLength)
        {
            errors.Add(path, $"The value must be {maximumLength} characters or fewer.");
        }

        return normalized;
    }

    private static (string Candidate, bool Repaired) ExtractCandidate(string rawJson)
    {
        var candidate = rawJson.Trim().TrimStart('\uFEFF').Trim();
        var repaired = !string.Equals(candidate, rawJson, StringComparison.Ordinal);
        var fenceStart = candidate.IndexOf("```", StringComparison.Ordinal);
        if (fenceStart >= 0)
        {
            var contentStart = candidate.IndexOf('\n', fenceStart + 3);
            var fenceEnd = contentStart >= 0
                ? candidate.IndexOf("```", contentStart + 1, StringComparison.Ordinal)
                : -1;
            if (contentStart >= 0 && fenceEnd > contentStart)
            {
                candidate = candidate[(contentStart + 1)..fenceEnd].Trim();
                repaired = true;
            }
        }

        var objectStart = candidate.IndexOf('{');
        var objectEnd = candidate.LastIndexOf('}');
        if (objectStart > 0 || objectEnd >= 0 && objectEnd < candidate.Length - 1)
        {
            if (objectStart >= 0 && objectEnd > objectStart)
            {
                candidate = candidate[objectStart..(objectEnd + 1)];
                repaired = true;
            }
        }

        return (candidate, repaired);
    }

    private static bool CanParseStrictly(string candidate)
    {
        try
        {
            using var _ = JsonDocument.Parse(candidate);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private sealed record ValidatedImport(
        QuizJsonImportDocumentV1 Document,
        QuizJsonImportPreview Preview);

    private sealed class ImportCounters
    {
        public int CollectionCount { get; set; }
        public int QuizCount { get; set; }
        public int RawItemCount { get; set; }
        public int WordCount { get; set; }
        public int SentenceCount { get; set; }
    }

    private sealed class ValidationErrors
    {
        private readonly Dictionary<string, List<string>> _errors = new(StringComparer.Ordinal);

        public int Count => _errors.Count;

        public void Add(string? path, string message)
        {
            var key = string.IsNullOrWhiteSpace(path) ? "$" : path;
            if (!_errors.TryGetValue(key, out var messages))
            {
                messages = [];
                _errors[key] = messages;
            }
            if (!messages.Contains(message, StringComparer.Ordinal))
            {
                messages.Add(message);
            }
        }

        public QuizJsonImportValidationException ToException(string? canonicalJson = null) =>
            new(
                _errors.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value.ToArray(),
                    StringComparer.Ordinal),
                canonicalJson);
    }
}
