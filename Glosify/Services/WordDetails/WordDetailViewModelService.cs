using Glosify.Data;
using Glosify.Models;
using Microsoft.EntityFrameworkCore;

namespace Glosify.Services;

public class WordDetailViewModelService : IWordDetailViewModelService
{
    private readonly GlosifyContext _context;

    public WordDetailViewModelService(GlosifyContext context)
    {
        _context = context;
    }

    public async Task<WordDetailViewModel?> BuildAsync(string wordDetailId, string userId)
    {
        var owned = await LoadAccessibleAsync(wordDetailId, userId);
        if (owned == null)
        {
            return null;
        }
        var (wordDetail, word, quiz) = owned.Value;

        return new WordDetailViewModel
        {
            Detail = wordDetail,
            Word = word,
            Quiz = quiz,
            Properties = WordDetailJsonReader.ReadProperties(wordDetail.Properties),
            Variants = WordDetailJsonReader.ReadVariants(wordDetail.Variants),
        };
    }

    private async Task<(WordDetail Detail, Word Word, Quiz Quiz)?> LoadAccessibleAsync(string id, string userId)
    {
        var pair = await (
            from word in _context.Words
            join quiz in _context.Quizzes on word.QuizId equals quiz.Id
            join detail in _context.WordDetails on word.WordDetailId equals detail.Id
            where detail.Id == id && quiz.UserId == userId
            select new { detail, word, quiz }).FirstOrDefaultAsync();

        return pair == null ? null : (pair.detail, pair.word, pair.quiz);
    }
}
