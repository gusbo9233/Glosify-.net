using Glosify.Models;

namespace Glosify.Services;

public interface IQuizService
{
    Task<Quiz?> FindQuizAsync(string userId, Guid? quizId);
    Task<Quiz?> GetQuizByIdAsync(Guid id, string userId);
    Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId);
    Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId);
    Task<bool> DeleteQuizAsync(Guid id, string userId);
    Task<int> GetAvailableWordCountAsync(Guid quizId);
}
