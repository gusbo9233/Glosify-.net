using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Glosify.Data;
using Glosify.Models.Library;
using Glosify.Services.Ai;
using Glosify.Services.Ai.Generation;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services.Books;

public sealed class BookPageTranslationService : IBookPageTranslationService
{
    internal const int CurrentSchemaVersion = 3;
    internal const int MaxSegments = 300;
    internal const int MaxSegmentCharacters = 2_000;
    internal const int MaxPageCharacters = 30_000;
    internal const int MaxBatchCharacters = 8_000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly GlosifyContext _context;
    private readonly IGenerativeAiClient _generativeAi;
    private readonly IBookPageTranslationCoordinator _coordinator;
    private readonly TimeProvider _timeProvider;

    public BookPageTranslationService(
        GlosifyContext context,
        IGenerativeAiClient generativeAi,
        IBookPageTranslationCoordinator coordinator,
        TimeProvider timeProvider)
    {
        _context = context;
        _generativeAi = generativeAi;
        _coordinator = coordinator;
        _timeProvider = timeProvider;
    }

    public async Task<string> UpdatePreferredLanguageAsync(
        Guid documentId,
        string userId,
        string? targetLanguage,
        CancellationToken cancellationToken = default)
    {
        var canonicalLanguage = ValidateLanguage(targetLanguage);
        var document = await _context.BookDocuments.FirstOrDefaultAsync(
            book => book.Id == documentId && book.UserId == userId,
            cancellationToken);
        if (document is null)
        {
            throw new BookPageTranslationNotFoundException();
        }

        if (!string.Equals(
                document.PreferredTranslationLanguage,
                canonicalLanguage,
                StringComparison.Ordinal))
        {
            document.PreferredTranslationLanguage = canonicalLanguage;
            document.UpdatedAt = _timeProvider.GetUtcNow();
            await _context.SaveChangesAsync(cancellationToken);
        }

        return canonicalLanguage;
    }

    public async Task<BookPageTranslationResult> TranslatePageAsync(
        Guid documentId,
        int pageNumber,
        string userId,
        string? targetLanguage,
        IReadOnlyList<BookPageSourceSegment>? segments,
        CancellationToken cancellationToken = default)
    {
        var canonicalLanguage = ValidateLanguage(targetLanguage);
        if (pageNumber <= 0)
        {
            throw new BookPageTranslationValidationException(
                "Page number must be greater than zero.");
        }

        var normalizedSegments = ValidateSegments(segments);
        var page = await _context.BookPages
            .AsNoTracking()
            .Include(item => item.BookDocument)
            .FirstOrDefaultAsync(
                item => item.BookDocumentId == documentId
                    && item.PageNumber == pageNumber
                    && item.BookDocument.UserId == userId,
                cancellationToken);
        if (page is null)
        {
            throw new BookPageTranslationNotFoundException();
        }

        var sourceHash = ComputeSourceHash(normalizedSegments);
        var cached = await FindCachedAsync(page.Id, canonicalLanguage, sourceHash, cancellationToken);
        if (cached is not null)
        {
            return MapCached(documentId, pageNumber, cached);
        }

        var cacheKey = $"{page.Id:N}:{canonicalLanguage}:{sourceHash}:{CurrentSchemaVersion}";
        await using var lease = await _coordinator.AcquireAsync(cacheKey, cancellationToken);

        cached = await FindCachedAsync(page.Id, canonicalLanguage, sourceHash, cancellationToken);
        if (cached is not null)
        {
            return MapCached(documentId, pageNumber, cached);
        }

        var model = ResolveTranslationModel();
        var operationId = Guid.NewGuid();
        var translatedByIndex = new Dictionary<int, string>();
        var modelsUsed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? detectedSourceLanguage = null;

        foreach (var batch in CreateBatches(normalizedSegments))
        {
            var usageContext = new AiUsageContext(
                userId,
                AiUsageFeatures.PageTranslation,
                "translate_book_page",
                operationId,
                "book_page",
                page.Id.ToString());
            var generated = await GenerateTranslationResponseAsync(
                batch,
                BuildPrompt(canonicalLanguage, batch),
                usageContext,
                model,
                cancellationToken);
            modelsUsed.Add(generated.Model);
            var repaired = await RepairUntranslatedSentencesAsync(
                batch,
                generated.Response,
                canonicalLanguage,
                usageContext,
                generated.Model,
                cancellationToken);
            modelsUsed.Add(repaired.Model);
            var response = repaired.Response;
            ValidateAiResponse(batch, response, canonicalLanguage);
            detectedSourceLanguage ??= NormalizeDetectedLanguage(response.DetectedSourceLanguage);
            foreach (var translated in response.Segments)
            {
                translatedByIndex.Add(translated.Index, translated.Translation.Trim());
            }
        }

        var translatedSegments = normalizedSegments
            .Select(segment => new BookPageTranslationSegment(
                segment.Index,
                segment.ParagraphIndex,
                segment.SourceText,
                translatedByIndex[segment.Index]))
            .ToArray();
        var generatedAt = _timeProvider.GetUtcNow();
        var entity = new BookPageTranslation
        {
            Id = Guid.NewGuid(),
            BookPageId = page.Id,
            TargetLanguage = canonicalLanguage,
            SourceTextHash = sourceHash,
            DetectedSourceLanguage = detectedSourceLanguage,
            SchemaVersion = CurrentSchemaVersion,
            Model = string.Join("+", modelsUsed),
            SegmentsJson = JsonSerializer.Serialize(translatedSegments, JsonOptions),
            CreatedAt = generatedAt,
        };

        _context.BookPageTranslations.Add(entity);
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            _context.Entry(entity).State = EntityState.Detached;
            cached = await FindCachedAsync(page.Id, canonicalLanguage, sourceHash, cancellationToken);
            if (cached is null)
            {
                throw;
            }

            return MapCached(documentId, pageNumber, cached);
        }

        return new BookPageTranslationResult(
            documentId,
            pageNumber,
            canonicalLanguage,
            detectedSourceLanguage,
            false,
            generatedAt,
            translatedSegments);
    }

    private async Task<BookPageTranslation?> FindCachedAsync(
        Guid pageId,
        string targetLanguage,
        string sourceHash,
        CancellationToken cancellationToken) =>
        await _context.BookPageTranslations
            .AsNoTracking()
            .FirstOrDefaultAsync(
                translation => translation.BookPageId == pageId
                    && translation.TargetLanguage == targetLanguage
                    && translation.SourceTextHash == sourceHash
                    && translation.SchemaVersion == CurrentSchemaVersion,
                cancellationToken);

    private static BookPageTranslationResult MapCached(
        Guid documentId,
        int pageNumber,
        BookPageTranslation cached)
    {
        var segments = JsonSerializer.Deserialize<BookPageTranslationSegment[]>(
            cached.SegmentsJson,
            JsonOptions);
        if (segments is null || segments.Length == 0)
        {
            throw new GenerativeAiStructuredOutputException(
                "The cached page translation could not be read.");
        }

        return new BookPageTranslationResult(
            documentId,
            pageNumber,
            cached.TargetLanguage,
            cached.DetectedSourceLanguage,
            true,
            cached.CreatedAt,
            segments);
    }

    private static string ValidateLanguage(string? language)
    {
        if (!BookTranslationLanguages.TryCanonicalize(language, out var canonicalLanguage))
        {
            throw new BookPageTranslationValidationException(
                "Choose a supported translation language.");
        }

        return canonicalLanguage;
    }

    private static IReadOnlyList<BookPageSourceSegment> ValidateSegments(
        IReadOnlyList<BookPageSourceSegment>? segments)
    {
        if (segments is null || segments.Count == 0)
        {
            throw new BookPageTranslationUnavailableException(
                "No selectable text found on this page.");
        }

        if (segments.Count > MaxSegments)
        {
            throw new BookPageTranslationValidationException(
                $"A page can contain at most {MaxSegments} translation segments.");
        }

        var normalized = new List<BookPageSourceSegment>(segments.Count);
        var totalCharacters = 0;
        var lastParagraph = 0;
        for (var index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            var sourceText = segment.SourceText?.Trim() ?? string.Empty;
            if (segment.Index != index)
            {
                throw new BookPageTranslationValidationException(
                    "Translation segment indices must be contiguous and start at zero.");
            }

            if (segment.ParagraphIndex < 0 || segment.ParagraphIndex < lastParagraph)
            {
                throw new BookPageTranslationValidationException(
                    "Translation paragraph indices must be non-negative and ordered.");
            }

            if (sourceText.Length == 0)
            {
                throw new BookPageTranslationValidationException(
                    "Translation segments cannot be empty.");
            }

            if (sourceText.Length > MaxSegmentCharacters)
            {
                throw new BookPageTranslationValidationException(
                    $"A translation segment can contain at most {MaxSegmentCharacters} characters.");
            }

            totalCharacters += sourceText.Length;
            if (totalCharacters > MaxPageCharacters)
            {
                throw new BookPageTranslationValidationException(
                    $"A page can contain at most {MaxPageCharacters} characters for translation.");
            }

            lastParagraph = segment.ParagraphIndex;
            normalized.Add(new BookPageSourceSegment(index, segment.ParagraphIndex, sourceText));
        }

        return normalized;
    }

    private static IReadOnlyList<IReadOnlyList<BookPageSourceSegment>> CreateBatches(
        IReadOnlyList<BookPageSourceSegment> segments)
    {
        var batches = new List<IReadOnlyList<BookPageSourceSegment>>();
        var current = new List<BookPageSourceSegment>();
        var currentCharacters = 0;
        foreach (var segment in segments)
        {
            if (current.Count > 0
                && currentCharacters + segment.SourceText.Length > MaxBatchCharacters)
            {
                batches.Add(current);
                current = [];
                currentCharacters = 0;
            }

            current.Add(segment);
            currentCharacters += segment.SourceText.Length;
        }

        if (current.Count > 0)
        {
            batches.Add(current);
        }

        return batches;
    }

    private static void ValidateAiResponse(
        IReadOnlyList<BookPageSourceSegment> requested,
        BookPageTranslationAiResponse? response,
        string targetLanguage)
    {
        ValidateAiResponseShape(requested, response);

        if (FindUntranslatedSentences(requested, response!, targetLanguage).Count > 0)
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service returned source text instead of a translation.");
        }
    }

    private static void ValidateAiResponseShape(
        IReadOnlyList<BookPageSourceSegment> requested,
        BookPageTranslationAiResponse? response)
    {
        if (response?.Segments is null || response.Segments.Count != requested.Count)
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service returned an incomplete page translation.");
        }

        var requestedIndices = requested.Select(segment => segment.Index).ToHashSet();
        var responseIndices = response.Segments.Select(segment => segment.Index).ToArray();
        if (responseIndices.Distinct().Count() != responseIndices.Length
            || responseIndices.Any(index => !requestedIndices.Contains(index))
            || response.Segments.Any(segment => string.IsNullOrWhiteSpace(segment.Translation)))
        {
            throw new GenerativeAiStructuredOutputException(
                "The AI service returned an invalid page translation.");
        }
    }

    private static IReadOnlyList<BookPageSourceSegment> FindUntranslatedSentences(
        IReadOnlyList<BookPageSourceSegment> requested,
        BookPageTranslationAiResponse response,
        string targetLanguage)
    {
        var detectedLanguage = NormalizeDetectedLanguage(response.DetectedSourceLanguage);
        if (string.IsNullOrWhiteSpace(detectedLanguage)
            || string.Equals(detectedLanguage, targetLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var translatedByIndex = response.Segments.ToDictionary(segment => segment.Index);
        return requested
            .Where(segment => LooksLikeSentence(segment.SourceText)
                && string.Equals(
                    NormalizeForHash(segment.SourceText),
                    NormalizeForHash(translatedByIndex[segment.Index].Translation),
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private async Task<(BookPageTranslationAiResponse Response, string Model)>
        RepairUntranslatedSentencesAsync(
        IReadOnlyList<BookPageSourceSegment> requested,
        BookPageTranslationAiResponse response,
        string targetLanguage,
        AiUsageContext usageContext,
        string model,
        CancellationToken cancellationToken)
    {
        var untranslated = FindUntranslatedSentences(requested, response, targetLanguage);
        if (untranslated.Count == 0) return (response, model);

        var generated = await GenerateTranslationResponseAsync(
            untranslated,
            BuildPrompt(targetLanguage, untranslated, isRepair: true),
            usageContext with { Operation = "repair_book_page_translation" },
            model,
            cancellationToken);
        var repaired = generated.Response;

        var merged = response.Segments.ToDictionary(segment => segment.Index);
        foreach (var segment in repaired.Segments)
        {
            merged[segment.Index] = segment;
        }

        return (
            new BookPageTranslationAiResponse
            {
                DetectedSourceLanguage = response.DetectedSourceLanguage
                    ?? repaired.DetectedSourceLanguage,
                Segments = requested.Select(segment => merged[segment.Index]).ToList(),
            },
            generated.Model);
    }

    private async Task<(BookPageTranslationAiResponse Response, string Model)>
        GenerateTranslationResponseAsync(
            IReadOnlyList<BookPageSourceSegment> requested,
            string prompt,
            AiUsageContext usageContext,
            string primaryModel,
            CancellationToken cancellationToken)
    {
        var attempts = new List<(string Model, string Operation, bool NativeStructuredOutput)>
        {
            (primaryModel, usageContext.Operation, true),
            (
                primaryModel,
                $"{usageContext.Operation}_retry",
                true),
        };

        for (var attemptIndex = 0; attemptIndex < attempts.Count; attemptIndex++)
        {
            var attempt = attempts[attemptIndex];
            try
            {
                var attemptUsage = usageContext with { Operation = attempt.Operation };
                var response = attempt.NativeStructuredOutput
                    ? await _generativeAi.GenerateStructuredAsync<BookPageTranslationAiResponse>(
                        prompt,
                        attemptUsage,
                        attempt.Model,
                        cancellationToken)
                    : await _generativeAi.GenerateJsonAsync<BookPageTranslationAiResponse>(
                        prompt,
                        attemptUsage,
                        attempt.Model,
                        cancellationToken);
                ValidateAiResponseShape(requested, response);
                return (response, attempt.Model);
            }
            catch (GenerativeAiStructuredOutputException)
                when (attemptIndex < attempts.Count - 1)
            {
                // Failed attempts release their reservation in the provider client.
                // Retry only typed output failures, never dependency, cancellation,
                // or credit errors.
            }
        }

        throw new InvalidOperationException("The translation attempt sequence was empty.");
    }

    private static bool LooksLikeSentence(string sourceText)
    {
        var normalized = sourceText.Trim();
        var withoutClosers = normalized.TrimEnd('"', '\'', '”', '’', '»', ')', ']');
        return normalized.Any(char.IsLetter)
            && withoutClosers.Length > 0
            && ".!?…".Contains(withoutClosers[^1]);
    }

    private static string BuildPrompt(
        string targetLanguage,
        IReadOnlyList<BookPageSourceSegment> segments,
        bool isRepair = false)
    {
        var payload = JsonSerializer.Serialize(
            segments.Select(segment => new { segment.Index, segment.SourceText }),
            JsonOptions);
        var repairInstruction = isRepair
            ? "A previous attempt copied these source sentences unchanged. Correct that failure and translate them now."
            : string.Empty;
        return $$"""
        Translate every input segment naturally into {{targetLanguage}}.
        {{repairInstruction}}

        Rules:
        - Return one output segment for every input segment.
        - Preserve each integer index exactly; do not merge, split, omit, or invent segments.
        - Translate the meaning in context while preserving names, numbers, and formatting that carries meaning.
        - Never copy a sentence unchanged when the detected source language differs from {{targetLanguage}}.
        - Return only data matching the requested structured response.
        - detectedSourceLanguage should be the language name detected in this batch.

        Return exactly this JSON object shape, with no markdown or extra keys:
        {"detectedSourceLanguage":"English","segments":[{"index":0,"translation":"Translated text"}]}
        Replace the example values and include one segments item for each input index.

        Input segments:
        {{payload}}
        """;
    }

    private static string ComputeSourceHash(IReadOnlyList<BookPageSourceSegment> segments)
    {
        var normalized = string.Join(
            '\n',
            segments.Select(segment =>
                $"{segment.Index}:{segment.ParagraphIndex}:{NormalizeForHash(segment.SourceText)}"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
    }

    private static string NormalizeForHash(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? NormalizeDetectedLanguage(string? language)
    {
        var normalized = language?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.Length <= 64
                ? normalized
                : normalized[..64];
    }

    private static string ResolveTranslationModel() => OpenAiModels.Luna;
}
