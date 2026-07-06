using Glosify.Models;

namespace Glosify.Services.Quizzes;

public interface IQuizService
{
    Task<Quiz?> FindQuizAsync(string userId, Guid? quizId);
    Task<Quiz?> GetQuizByIdAsync(Guid id, string userId);
    Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId);
    Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId, Guid? collectionId = null);
    Task<Quiz?> DeleteQuizAsync(Guid id, string userId);
    Task<bool> UserOwnsQuizAsync(Guid quizId, string userId);
    Task<bool> SetQuizPublicAsync(Guid id, string userId, bool isPublic);
    Task<IReadOnlyList<Quiz>> GetPublicQuizzesAsync(string language);
    Task<Quiz?> GetPublicQuizAsync(Guid id);
    Task<Quiz?> CopyPublicQuizAsync(Guid id, string userId, Guid? collectionId = null);
    Task<int> GetAvailableWordCountAsync(Guid quizId);
    Task<IReadOnlyDictionary<Guid, int>> GetWordCountsAsync(IReadOnlyCollection<Guid> quizIds);
    Task<int> GetAvailableSentenceCountAsync(Guid quizId);
}
