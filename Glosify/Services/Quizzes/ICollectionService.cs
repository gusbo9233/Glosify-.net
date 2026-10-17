using Glosify.Models.Entities;
namespace Glosify.Services.Quizzes;

public interface ICollectionService
{
    Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language, CancellationToken cancellationToken = default);
    Task<Collection?> GetCollectionAsync(Guid id, string userId, CancellationToken cancellationToken = default);
    Task<Collection> CreateCollectionAsync(string name, string language, string userId, Guid? parentCollectionId, CancellationToken cancellationToken = default);
    Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId, CancellationToken cancellationToken = default);
    Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId, CancellationToken cancellationToken = default);
    Task<bool> DeleteCollectionAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
    Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId, CancellationToken cancellationToken = default);
}
