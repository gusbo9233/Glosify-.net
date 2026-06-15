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

        var result = await _vocabularyGenerator.RepairWordAsync(repairData, wordId, userId, cancellationToken);
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

        var result = await _vocabularyGenerator.RepairSentenceAsync(repairData, sentenceText, userId, cancellationToken);
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
            .OrderBy(word => word.Lemma)
            .ToListAsync(cancellationToken);

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
                .Select(word => new RepairWord
                {
                    Id = word.Id,
                    Word = word.Lemma,
                    Translation = word.Translation,
                    QuizId = word.QuizId.ToString()
                })
                .ToList(),
            Sentences = repairSentences
        };
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

        await _context.SaveChangesAsync(cancellationToken);
    }

    private async Task<int> ApplySentenceAsync(
        Guid quizId,
        string originalText,
        RepairSentence repaired,
        CancellationToken cancellationToken)
    {
        var normalizedOriginal = originalText.Trim();
        var sentences = await _context.QuizSentences
            .Where(sentence => sentence.QuizId == quizId)
            .ToListAsync(cancellationToken);
        var matches = sentences
            .Where(sentence => string.Equals(
                sentence.Text.Trim(),
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

}
