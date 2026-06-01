using System.Text.Json;
using Glosify.Data;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public sealed class QuizRepairService : IQuizRepairService
{
    private readonly GlosifyContext _context;
    private readonly IQuizService _quizService;
    private readonly IVocabularyGenerationService _vocabularyGenerator;

    public QuizRepairService(
        GlosifyContext context,
        IQuizService quizService,
        IVocabularyGenerationService vocabularyGenerator)
    {
        _context = context;
        _quizService = quizService;
        _vocabularyGenerator = vocabularyGenerator;
    }

    public async Task<QuizRepairResult> RepairQuizAsync(Guid quizId, string userId, CancellationToken cancellationToken)
    {
        var repairData = await BuildRepairDataAsync(quizId, userId, cancellationToken);
        if (repairData == null)
        {
            return new QuizRepairResult(QuizRepairStatus.NotFound);
        }

        var result = await _vocabularyGenerator.RepairQuizAsync(repairData, cancellationToken);
        if (result?.QuizData == null)
        {
            return new QuizRepairResult(QuizRepairStatus.LlmUnavailable);
        }

        await ApplyQuizAsync(quizId, result.QuizData, cancellationToken);
        return new QuizRepairResult(QuizRepairStatus.Success);
    }

    public async Task<QuizRepairResult> RepairWordAsync(string wordId, string userId, CancellationToken cancellationToken)
    {
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == wordId, cancellationToken);
        if (word == null)
        {
            return new QuizRepairResult(QuizRepairStatus.NotFound);
        }

        var repairData = await BuildRepairDataAsync(word.QuizId, userId, cancellationToken);
        if (repairData == null)
        {
            return new QuizRepairResult(QuizRepairStatus.NotFound);
        }

        var result = await _vocabularyGenerator.RepairWordAsync(repairData, wordId, cancellationToken);
        if (result?.Word == null || string.IsNullOrWhiteSpace(result.Word.Id))
        {
            return new QuizRepairResult(QuizRepairStatus.LlmUnavailable);
        }

        await ApplyWordAsync(result, cancellationToken);
        return new QuizRepairResult(QuizRepairStatus.Success, Word: word.Lemma);
    }

    public async Task<QuizRepairResult> RepairSentenceAsync(
        Guid quizId,
        string sentenceText,
        string userId,
        CancellationToken cancellationToken)
    {
        var repairData = await BuildRepairDataAsync(quizId, userId, cancellationToken);
        if (repairData == null)
        {
            return new QuizRepairResult(QuizRepairStatus.NotFound);
        }

        var result = await _vocabularyGenerator.RepairSentenceAsync(repairData, sentenceText, cancellationToken);
        if (result?.Sentence == null || string.IsNullOrWhiteSpace(result.Sentence.Text))
        {
            return new QuizRepairResult(QuizRepairStatus.LlmUnavailable);
        }

        var updatedCount = await ApplySentenceAsync(quizId, sentenceText, result.Sentence, cancellationToken);
        return new QuizRepairResult(QuizRepairStatus.Success, UpdatedCount: updatedCount);
    }

    private async Task<RepairQuizData?> BuildRepairDataAsync(Guid quizId, string userId, CancellationToken cancellationToken)
    {
        var quiz = await _quizService.GetQuizByIdAsync(quizId, userId);
        if (quiz == null)
        {
            return null;
        }

        var rows = await _context.Words
            .Where(word => word.QuizId == quizId)
            .GroupJoin(
                _context.WordDetails,
                word => word.WordDetailId,
                detail => detail.Id,
                (word, details) => new { Word = word, Detail = details.FirstOrDefault() })
            .OrderBy(row => row.Word.Lemma)
            .ToListAsync(cancellationToken);

        var details = rows
            .Select(row => row.Detail)
            .OfType<WordDetail>()
            .GroupBy(detail => detail.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToList();

        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .OrderBy(sentence => sentence.Text)
            .ToListAsync(cancellationToken);

        var repairSentences = sentences
            .Select((group, index) => new RepairSentence
            {
                Id = $"s{index + 1:000}",
                Text = group.Text,
                Translation = group.Translation,
                QuizId = quiz.Id.ToString()
            })
            .ToList();

        return new RepairQuizData
        {
            Quiz = new RepairQuiz
            {
                Id = quiz.Id.ToString(),
                Name = quiz.Name,
                SourceLanguage = quiz.SourceLanguage,
                TargetLanguage = quiz.TargetLanguage,
                Language = string.IsNullOrWhiteSpace(quiz.Language)
                    ? quiz.TargetLanguage.ToLowerInvariant()
                    : quiz.Language.ToLowerInvariant(),
                ProcessingStatus = quiz.ProcessingStatus,
                ProcessingMessage = quiz.ProcessingMessage
            },
            Words = rows
                .Select(row => new RepairWord
                {
                    Id = row.Word.Id,
                    Word = row.Word.Lemma,
                    Translation = row.Word.Translation,
                    WordDetailId = row.Word.WordDetailId ?? string.Empty,
                    QuizId = row.Word.QuizId.ToString()
                })
                .ToList(),
            WordDetails = details
                .Select(ToRepairWordDetail)
                .ToList(),
            Sentences = repairSentences
        };
    }

    private async Task ApplyQuizAsync(Guid quizId, RepairQuizData repaired, CancellationToken cancellationToken)
    {
        var wordsById = await _context.Words
            .Where(word => word.QuizId == quizId)
            .ToDictionaryAsync(word => word.Id, cancellationToken);
        var detailsById = await _context.WordDetails
            .Where(detail => wordsById.Values.Select(word => word.WordDetailId).Contains(detail.Id))
            .ToDictionaryAsync(detail => detail.Id, cancellationToken);

        foreach (var repairedWord in repaired.Words)
        {
            if (!wordsById.TryGetValue(repairedWord.Id, out var word))
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(repairedWord.Word))
            {
                word.Lemma = repairedWord.Word.Trim();
            }
            if (!string.IsNullOrWhiteSpace(repairedWord.Translation))
            {
                word.Translation = repairedWord.Translation.Trim();
            }
        }

        foreach (var repairedDetail in repaired.WordDetails)
        {
            if (detailsById.TryGetValue(repairedDetail.Id, out var detail))
            {
                ApplyRepairedWordDetail(detail, repairedDetail);
            }
        }

        await ApplyQuizSentencesAsync(quizId, repaired.Sentences, cancellationToken);

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task ApplyWordAsync(RepairWordResult result, CancellationToken cancellationToken)
    {
        var word = await _context.Words.FirstOrDefaultAsync(w => w.Id == result.Word.Id, cancellationToken);
        if (word == null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(result.Word.Word))
        {
            word.Lemma = result.Word.Word.Trim();
        }
        if (!string.IsNullOrWhiteSpace(result.Word.Translation))
        {
            word.Translation = result.Word.Translation.Trim();
        }

        var detail = await _context.WordDetails.FirstOrDefaultAsync(d => d.Id == word.WordDetailId, cancellationToken);
        if (detail != null)
        {
            result.WordDetail.Id = detail.Id;
            ApplyRepairedWordDetail(detail, result.WordDetail);
        }

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ApplySentenceAsync(
        Guid quizId,
        string originalText,
        RepairSentence repaired,
        CancellationToken cancellationToken)
    {
        var normalizedOriginal = VocabularyInputCleaner.CleanForVocabulary(originalText).Trim();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .ToListAsync(cancellationToken);
        var matches = sentences
            .Where(sentence => string.Equals(
                VocabularyInputCleaner.CleanForVocabulary(sentence.Text).Trim(),
                normalizedOriginal,
                StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var sentence in matches)
        {
            sentence.Text = repaired.Text.Trim();
            sentence.Translation = repaired.Translation.Trim();
        }

        await _context.SaveChangesAsync(cancellationToken);
        return matches.Count;
    }

    private static RepairWordDetail ToRepairWordDetail(WordDetail detail)
    {
        return new RepairWordDetail
        {
            Id = detail.Id,
            Properties = ParseJsonObject(detail.Properties),
            ExampleSentence = detail.ExampleSentence,
            ExampleSentenceTranslation = detail.ExampleSentenceTranslation,
            Explanation = detail.Explanation,
            Variants = ParseVariants(detail.Variants),
            Language = string.IsNullOrWhiteSpace(detail.Language)
                ? detail.TargetLanguage.ToLowerInvariant()
                : detail.Language.ToLowerInvariant()
        };
    }

    private static void ApplyRepairedWordDetail(WordDetail detail, RepairWordDetail repaired)
    {
        if (repaired.Properties.Count > 0)
        {
            detail.Properties = JsonSerializer.Serialize(repaired.Properties);
        }
        if (repaired.Variants.Count > 0)
        {
            detail.Variants = JsonSerializer.Serialize(repaired.Variants);
        }
        if (!string.IsNullOrWhiteSpace(repaired.Explanation))
        {
            detail.Explanation = repaired.Explanation.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.ExampleSentence))
        {
            detail.ExampleSentence = repaired.ExampleSentence.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.ExampleSentenceTranslation))
        {
            detail.ExampleSentenceTranslation = repaired.ExampleSentenceTranslation.Trim();
        }
        if (!string.IsNullOrWhiteSpace(repaired.Language))
        {
            detail.Language = repaired.Language.Trim();
        }

        detail.UpdatedAt = DateTimeOffset.UtcNow;
    }

    private async Task ApplyQuizSentencesAsync(
        Guid quizId,
        IReadOnlyList<RepairSentence> repairedSentences,
        CancellationToken cancellationToken)
    {
        if (repairedSentences.Count == 0)
        {
            return;
        }

        var existing = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .ToListAsync(cancellationToken);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var now = DateTimeOffset.UtcNow;

        foreach (var repaired in repairedSentences)
        {
            var text = repaired.Text.Trim();
            if (string.IsNullOrWhiteSpace(text) || !used.Add(text))
            {
                continue;
            }

            var sentence = existing.FirstOrDefault(candidate =>
                string.Equals(candidate.Text.Trim(), text, StringComparison.OrdinalIgnoreCase));
            if (sentence == null)
            {
                _context.QuizSentences.Add(new QuizSentence
                {
                    Id = Guid.NewGuid(),
                    QuizId = quizId,
                    Text = text,
                    Translation = repaired.Translation.Trim(),
                    CreatedAt = now
                });
                continue;
            }

            sentence.Text = text;
            sentence.Translation = repaired.Translation.Trim();
        }
    }

    private static Dictionary<string, JsonElement> ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "{}")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static List<GeneratedWordVariant> ParseVariants(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Trim() == "[]")
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<GeneratedWordVariant>>(json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

}
