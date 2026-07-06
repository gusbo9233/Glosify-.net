namespace Glosify.Services.Quizzes
{
    /// <summary>
    /// A public root collection together with the counts the Explore cards show:
    /// descendant collections (excluding the root) and quizzes across the whole subtree.
    /// </summary>
    public sealed record PublicCollectionSummary(Collection Collection, int CollectionCount, int QuizCount);

    public interface ICollectionService
    {
        Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language);
        Task<Collection?> GetCollectionAsync(Guid id, string userId);
        Task<Collection> CreateCollectionAsync(string name, string language, string userId, Guid? parentCollectionId);
        Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId);
        Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId);
        Task<bool> DeleteCollectionAsync(Guid collectionId, string userId);
        Task<bool> SetCollectionPublicAsync(Guid collectionId, string userId, bool isPublic);
        Task<IReadOnlyList<Collection>> GetPublicCollectionRootsAsync(string language);
        Task<IReadOnlyList<PublicCollectionSummary>> GetPublicCollectionSummariesAsync(string language);
        Task<Collection?> GetPublicCollectionTreeAsync(Guid collectionId);
        Task<Collection?> CopyPublicCollectionAsync(Guid collectionId, string userId);
        Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId);
    }
}
