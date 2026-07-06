namespace Glosify.Services.Quizzes
{
    /// <summary>
    /// A public root collection together with the counts the Explore cards show:
    /// descendant collections (excluding the root) and quizzes across the whole subtree.
    /// </summary>
    public sealed record PublicCollectionSummary(Collection Collection, int CollectionCount, int QuizCount);

    public interface ICollectionService
    {
        Task<IReadOnlyList<Collection>> GetCollectionsAsync(string userId, string language, CancellationToken cancellationToken = default);
        Task<Collection?> GetCollectionAsync(Guid id, string userId, CancellationToken cancellationToken = default);
        Task<Collection> CreateCollectionAsync(string name, string language, string userId, Guid? parentCollectionId, CancellationToken cancellationToken = default);
        Task<bool> MoveCollectionAsync(Guid collectionId, Guid? parentCollectionId, string userId, CancellationToken cancellationToken = default);
        Task<bool> RenameCollectionAsync(Guid collectionId, string name, string userId, CancellationToken cancellationToken = default);
        Task<bool> DeleteCollectionAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
        Task<bool> SetCollectionPublicAsync(Guid collectionId, string userId, bool isPublic, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<Collection>> GetPublicCollectionRootsAsync(string language, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<PublicCollectionSummary>> GetPublicCollectionSummariesAsync(string language, CancellationToken cancellationToken = default);
        Task<Collection?> GetPublicCollectionTreeAsync(Guid collectionId, CancellationToken cancellationToken = default);
        Task<Collection?> CopyPublicCollectionAsync(Guid collectionId, string userId, CancellationToken cancellationToken = default);
        Task<bool> MoveQuizToCollectionAsync(Guid quizId, Guid? collectionId, string userId, CancellationToken cancellationToken = default);
    }
}
