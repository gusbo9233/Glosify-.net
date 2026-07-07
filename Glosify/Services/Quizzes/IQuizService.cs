using Glosify.Models;

namespace Glosify.Services.Quizzes;

public interface IQuizService
{
    Task<Quiz?> FindQuizAsync(string userId, Guid? quizId, CancellationToken cancellationToken = default);
    Task<Quiz?> GetQuizByIdAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Quiz>> GetUserQuizzesAsync(string userId, CancellationToken cancellationToken = default);
    Task<Quiz> CreateQuizAsync(string name, string sourceLanguage, string targetLanguage, string userId, Guid? collectionId = null, CancellationToken cancellationToken = default);
    Task<Quiz?> DeleteQuizAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<bool> UserOwnsQuizAsync(Guid quizId, string userId, CancellationToken cancellationToken = default);
    Task<bool> SetQuizPublicAsync(Guid id, string userId, bool isPublic, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<Quiz>> GetPublicQuizzesAsync(string language, CancellationToken cancellationToken = default);
    Task<Quiz?> GetPublicQuizAsync(Guid id, CancellationToken cancellationToken = default);
    Task<Quiz?> CopyPublicQuizAsync(Guid id, string userId, Guid? collectionId = null, CancellationToken cancellationToken = default);
    Task<Quiz?> CopyClassroomQuizAsync(Guid id, Guid classroomId, string userId, CancellationToken cancellationToken = default);
    Task<int> GetAvailableWordCountAsync(Guid quizId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<Guid, int>> GetWordCountsAsync(IReadOnlyCollection<Guid> quizIds, CancellationToken cancellationToken = default);
    Task<int> GetAvailableSentenceCountAsync(Guid quizId, CancellationToken cancellationToken = default);
}
