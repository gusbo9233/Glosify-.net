namespace Glosify.Services.Quizzes
{
    public interface ICollectionService
    {
        Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language);
        Task<Collection?> GetCollectionAsync(Guid id, string userId);
        Task<Collection> CreateCollectionAsync(string name, string language, string userId, Guid? parentCollectionId);
        Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId);
        Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId);
        Task<bool> DeleteCollectionAsync(Guid collectionId, string userId);
        Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId);
    }
}
