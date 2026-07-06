namespace Glosify.Models.Api;

public sealed record CollectionDto(Guid Id, string Name, string Language, Guid? ParentCollectionId, bool IsPublic)
{
    public static CollectionDto From(Collection collection) =>
        new(collection.Id, collection.Name, collection.Language, collection.ParentCollectionId, collection.IsPublic);
}

public sealed record CreateCollectionRequest(string Name, string Language, Guid? ParentCollectionId);

public sealed record RenameCollectionRequest(string Name);

public sealed record MoveQuizRequest(Guid? CollectionId);
