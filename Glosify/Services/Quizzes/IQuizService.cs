using Glosify.Models;

namespace Glosify.Services;

public interface IQuizService
{
    Task<Quiz?> FindQuizAsync(string userId, Guid? quizId);
    Task<Quiz?> GetQuizByIdAsync(Guid id, string userId);
    Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId);
    Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId);
    Task<Quiz?> DeleteQuizAsync(Guid id, string userId);
    Task<bool> UserOwnsQuizAsync(Guid quizId, string userId);
    Task<int> GetAvailableWordCountAsync(Guid quizId);
}
